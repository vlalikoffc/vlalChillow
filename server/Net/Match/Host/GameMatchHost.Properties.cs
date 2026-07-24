using System.Buffers.Binary;
using System.Net;
using LiteNetLib;
using StandChillow.LanServer.Lan;
using StandChillow.LanServer.Net.Lobby;
using StandChillow.LanServer.Net.Match;
using StandChillow.LanServer.Net.Match.Host;

namespace StandChillow.LanServer.Net;

public sealed partial class GameMatchHost
{
    private void HandleSetProperty(NetPeer peer, MatchFrameFlags flags, byte[] raw)
    {
        DumpCapture("match_rx_SetProperty", raw);
        if ((flags & MatchFrameFlags.IsEncrypted) != 0)
        {
            Console.WriteLine($"[match-host] SetProperty flags={flags} encrypted — dump only");
            return;
        }

        try
        {
            if (!MatchCodec.TryOpenBody(raw, out _, out _, out _, out var bodyBytes))
            {
                Console.WriteLine(
                    $"[match-host] SetProperty flags={flags} — could not open body (len={raw.Length})");
                return;
            }

            var body = new LobbyReader(bodyBytes);
            var (actorNr, key, value) = MatchCodec.ParseSetPropertyBody(body);
            var compressed = (flags & MatchFrameFlags.IsCompressed) != 0;

            if (!_peers.TryGetValue(peer, out var st) || st.Room is null)
                return;
            var room = st.Room;

            // Host-owned room keys (actor=0): clients must not author C2 / scores / timers.
            // Blind relay of client CtScore/TrScore/C2 caused Escalation "Prep without RoundEnd"
            // and double round scoring when a later host RoundEnd also bumped Flow scores.
            if (actorNr == 0 && IsHostOwnedRoomPropKey(key))
            {
                Console.WriteLine(
                    $"[match-host] SetProperty actor=0 key='{key}' REJECTED " +
                    "(host-owned room prop — score only at RoundEnd; C2/Time from match-flow)");
                return;
            }

            // Ping: do not echo client estimate (often ≈0/1/2). Phone host path measures
            // LiteNetLib latency (grp.OnNetworkLatencyUpdate); we write RoundTripTime ms.
            var pingFromRtt = false;
            if (key == MatchRoomPropKeys.Ping)
            {
                var rtt = Math.Max(0, peer.RoundTripTime);
                value = LobbyVariant.FromInt(rtt);
                pingFromRtt = true;
            }

            Console.WriteLine(
                $"[match-host] SetProperty actor={actorNr} key='{key}' value={value}" +
                (compressed ? " (LZ4→echo uncompressed HasServerTime)" : "") +
                (pingFromRtt ? $" (server RTT={peer.RoundTripTime}ms, not client echo)" : ""));

            LobbyVariant prevValue = default;
            bool hadPrev;
            lock (_roomGate)
            {
                hadPrev = room.ActorProps.TryGetValue((actorNr, key), out prevValue);
                room.ActorProps[(actorNr, key)] = value;
            }

            // Rebroadcast promptly (observe = ActorProps write above; no Task.Delay).
            // Combat side-effects (master Destroy+death / wipe) run same-tick AFTER echo so
            // the client's packet is not blocked by host-authored follow-up TX.
            var outPkt = MatchCodec.BuildSetProperty(NextServerTime(), actorNr, key, value);
            BroadcastRoom(room, outPkt, tag: "match_tx_SetProperty");

            // Diagnostics: combat counters + buy/loadout props (single-key path).
            LogTdmCombatProp(room, actorNr, key, value, hadPrev ? prevValue : default, hadPrev);
            LogTdmBuyProp(actorNr, key, value);

            // Joiner-ready gate: identity batch before host managers+C2.
            NoteJoinerIdentityProp(peer, actorNr, key);

            // Fighting pawn: phone joiner allocates CreateWorldObject (often id≠129) after
            // team pick — do not host-spawn id=129 here (that capture was host-self).
            if (key == MatchRoomPropKeys.Team
                && value.Kind == LobbyVariantKind.Byte
                && actorNr != MatchHostActor.ActorNr)
            {
                var team = (MatchTeam)value.Byte;
                if (team is MatchTeam.Tr or MatchTeam.Ct)
                {
                    Console.WriteLine(
                        $"[match-host] team={team} actor={actorNr} — await joiner CreateWorldObject " +
                        $"(echo+rpc1+state; wire name Tr_Tr/Ct_Ct + fusTag=101 looks like *e in ASCII)");
                    TryBeginMatchFlowIfBothTeams(room);
                    TryAssignBomberOnTrJoin(room, actorNr);
                }
            }

            if (key == MatchRoomPropKeys.Death)
            {
                Console.WriteLine(
                    $"[observe] SetProperty death actor={actorNr} value={value} (same-tick wipe)");
                // Duel FFA: AuthorDuelFfaDeath (Ranked2v2 NoteActorDeath ignores PreWarmup).
                if (!TryHandleDuelDeathProp(room, actorNr, value, source: "SetProperty"))
                    NoteActorDeath(room, actorNr, value, source: "SetProperty");
            }

            // Killer client owns kills — increment = confirmed elimination. Room master must
            // author victim Destroy + death (MATCH_WORLD / TDM gold); relay alone desyncs HP/death.
            if (key == MatchRoomPropKeys.Kills
                && VariantAsInt(value) > (hadPrev ? VariantAsInt(prevValue) : 0))
            {
                Console.WriteLine(
                    $"[observe] SetProperty kills++ actor={actorNr} " +
                    $"{(hadPrev ? VariantAsInt(prevValue) : 0)}→{VariantAsInt(value)} " +
                    "(same-tick master death + wipe)");
                // Duel FFA branches inside; TDM / Allies / bomb paths unchanged vs HEAD.
                RouteConfirmedKillByKiller(room, actorNr);
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[match-host] SetProperty parse failed: {FormatException(ex)}");
        }
    }

    private void HandleSetProperties(NetPeer peer, MatchFrameFlags flags, byte[] raw)
    {
        DumpCapture("match_rx_SetProperties", raw);
        if ((flags & MatchFrameFlags.IsEncrypted) != 0)
        {
            Console.WriteLine($"[match-host] SetProperties flags={flags} encrypted — dump only");
            return;
        }

        try
        {
            if (!MatchCodec.TryOpenBody(raw, out _, out _, out _, out var bodyBytes))
            {
                Console.WriteLine(
                    $"[match-host] SetProperties flags={flags} — could not open body (len={raw.Length})");
                return;
            }

            var body = new LobbyReader(bodyBytes);
            var (actorNr, props) = MatchCodec.ParseSetPropertiesBody(body);
            Console.WriteLine(
                $"[match-host] SetProperties actor={actorNr} count={props.Count} " +
                $"keys=[{string.Join(",", props.Select(p => p.Key))}]");

            if (!_peers.TryGetValue(peer, out var st) || st.Room is null)
                return;
            var room = st.Room;
            var killsIncreased = false;
            var diag = new List<(string Key, LobbyVariant Old, bool Had, LobbyVariant New)>(props.Count);
            var accepted = new List<(string Key, LobbyVariant Value)>(props.Count);
            lock (_roomGate)
            {
                foreach (var (k, v) in props)
                {
                    if (actorNr == 0 && IsHostOwnedRoomPropKey(k))
                    {
                        Console.WriteLine(
                            $"[match-host] SetProperties actor=0 key='{k}' REJECTED " +
                            "(host-owned room prop — not applied/relayed)");
                        continue;
                    }

                    var had = room.ActorProps.TryGetValue((actorNr, k), out var prevK);
                    diag.Add((k, had ? prevK : default, had, v));
                    if (k == MatchRoomPropKeys.Kills)
                    {
                        var oldK = had ? VariantAsInt(prevK) : 0;
                        if (VariantAsInt(v) > oldK)
                            killsIncreased = true;
                    }
                    if (v.Kind == LobbyVariantKind.Null)
                        room.ActorProps.Remove((actorNr, k));
                    else
                        room.ActorProps[(actorNr, k)] = v;
                    if (actorNr == 0 && k == MatchRoomPropKeys.C2 && v.Kind == LobbyVariantKind.Byte)
                        room.RoomC2 = v.Byte;
                    accepted.Add((k, v));
                }
            }

            if (accepted.Count == 0)
                return;

            // Echo promptly after ActorProps observe (no Task.Delay). Combat follow-ups below.
            var outPkt = MatchCodec.BuildSetProperties(NextServerTime(), actorNr, accepted);
            BroadcastRoom(room, outPkt, tag: "match_tx_SetProperties");

            // Diagnostics: combat counters + buy/loadout props (bag path).
            foreach (var (k, old, had, nv) in diag)
            {
                LogTdmCombatProp(room, actorNr, k, nv, old, had);
                LogTdmBuyProp(actorNr, k, nv);
            }

            foreach (var (k, v) in accepted)
            {
                if (k == MatchRoomPropKeys.Team
                    && v.Kind == LobbyVariantKind.Byte
                    && actorNr != MatchHostActor.ActorNr
                    && (MatchTeam)v.Byte is MatchTeam.Tr or MatchTeam.Ct)
                {
                    Console.WriteLine(
                        $"[match-host] team bag actor={actorNr} — await joiner CreateWorldObject");
                    TryBeginMatchFlowIfBothTeams(room);
                    TryAssignBomberOnTrJoin(room, actorNr);
                }

                if (k == MatchRoomPropKeys.Death)
                {
                    Console.WriteLine(
                        $"[observe] SetProperties death actor={actorNr} value={v} (same-tick wipe)");
                    NoteActorDeath(room, actorNr, v, source: "SetProperties");
                }
            }

            // kills++ = confirmed kill. Duel FFA branches in RouteConfirmedKillByKiller;
            // TDM / Allies / Escalation / Defuse paths unchanged.
            if (killsIncreased)
            {
                Console.WriteLine(
                    $"[observe] SetProperties kills++ actor={actorNr} " +
                    "(same-tick master death + wipe)");
                RouteConfirmedKillByKiller(room, actorNr);
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[match-host] SetProperties parse failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Room props the dedicated host alone may publish on actor=0. Client writes are rejected
    /// so TrScore/CtScore cannot bump on kill (TDM bleed into bomb modes) and C2 cannot skip
    /// RoundEnd UI.
    /// </summary>
    private static bool IsHostOwnedRoomPropKey(string key) =>
        key is MatchRoomPropKeys.C2
            or MatchRoomPropKeys.Time
            or MatchRoomPropKeys.Round
            or MatchRoomPropKeys.RoundCount
            or MatchRoomPropKeys.RoundStartTime
            or MatchRoomPropKeys.TrScore
            or MatchRoomPropKeys.CtScore
            or MatchRoomPropKeys.TrCoLosses
            or MatchRoomPropKeys.CtCoLosses
            or MatchRoomPropKeys.WinTeam
            or MatchRoomPropKeys.BomberId
            or MatchRoomPropKeys.BombSite
            or MatchRoomPropKeys.CtRoundStartPlayersCount
            or MatchRoomPropKeys.TrRoundStartPlayersCount
            or MatchRoomPropKeys.FinalWinTeam
            or MatchRoomPropKeys.MvpPlayer
            or MatchRoomPropKeys.CurrentLoadout
            or MatchRoomPropKeys.CurrentRoundModifierId
            or MatchRoomPropKeys.UsedRoundModifierIds;

    /// <summary>
    /// Verbose [tdm-death] trace for the combat counters that drive the master-death path
    /// (<c>kills</c>/<c>fair_kills</c>/<c>death</c>): logs old→new and whether the change is the
    /// signal that triggers <see cref="NoteDeathMatchKillByKiller"/> (kills++) or
    /// <see cref="NoteActorDeath"/> (death). DeathMatch rooms only; no-op otherwise.
    /// </summary>
    private void LogTdmCombatProp(
        MatchRoom room, byte actorNr, string key, LobbyVariant value, LobbyVariant prev, bool hadPrev)
    {
        if (!IsDeathMatchRoom(room))
            return;
        if (key is not (MatchRoomPropKeys.Kills or MatchRoomPropKeys.FairKills
            or MatchRoomPropKeys.Death or MatchRoomPropKeys.Score2 or MatchRoomPropKeys.RoundKills))
            return;

        var oldV = hadPrev ? VariantAsInt(prev) : 0;
        var newV = VariantAsInt(value);
        string effect = key switch
        {
            MatchRoomPropKeys.Kills when newV > oldV =>
                "→ confirmed kill: NoteDeathMatchKillByKiller (master-author victim death)",
            MatchRoomPropKeys.Death when newV != 0 =>
                "→ death prop: NoteActorDeath (fallback master-author for this victim)",
            _ => "(no death trigger)",
        };
        Console.WriteLine(
            $"[tdm-death] prop actor={actorNr} {key} {oldV}→{newV} {effect}");
    }

    /// <summary>
    /// Verbose [tdm-buy] trace for buy/loadout/character props so the free-buy work is diagnosable.
    /// The weapon/character pick itself rides in the pawn CreateWorldObject trailing (see
    /// <c>HandleCreateWorldObject</c> / [tdm-buy] pawn CWO log); glove/skin ids arrive as their own
    /// keys. Logs any known cosmetic key plus anything that looks buy/weapon-related.
    /// </summary>
    private static void LogTdmBuyProp(byte actorNr, string key, LobbyVariant value)
    {
        var known = key is MatchRoomPropKeys.GlovesIdCt or MatchRoomPropKeys.GlovesIdTr
            or MatchRoomPropKeys.Money;
        var looksBuy = key.Contains("weapon", StringComparison.OrdinalIgnoreCase)
            || key.Contains("gun", StringComparison.OrdinalIgnoreCase)
            || key.Contains("buy", StringComparison.OrdinalIgnoreCase)
            || key.Contains("skin", StringComparison.OrdinalIgnoreCase)
            || key.Contains("loadout", StringComparison.OrdinalIgnoreCase)
            || key.Contains("gloves", StringComparison.OrdinalIgnoreCase);
        if (!known && !looksBuy)
            return;
        Console.WriteLine(
            $"[tdm-buy] prop actor={actorNr} {key}={value}" +
            (looksBuy && !known ? " (unknown buy-like key — relayed generically, logged for reverse)" : ""));
    }
}

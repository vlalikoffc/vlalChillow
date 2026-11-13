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
    private void NotifyPeersActorJoined(
        MatchRoom room, NetPeer joinerPeer, byte joinerActorNr, string joinerName)
    {
        if (joinerActorNr == MatchHostActor.ActorNr)
            return;

        _peers.TryGetValue(joinerPeer, out var joinerState);
        var evt = MatchCodec.BuildActorJoinedEvent(
            NextServerTime(),
            new MatchGapActor
            {
                ActorNr = joinerActorNr,
                Name = joinerName,
                UserId = joinerState?.UserId ?? "",
                Props = null,
                Flag = false,
            });
        var n = BroadcastRoomExcept(room, joinerPeer, evt, tag: "match_tx_ActorJoinedEvent");
        Console.WriteLine(
            $"[match-host] TX ActorJoinedEvent actor={joinerActorNr} name='{joinerName}' " +
            $"→ peers={n} (dynamic join notify)");
    }

    /// <summary>
    /// After Found: do <b>not</b> immediately TX managers+C2. Phone host waits for joiner
    /// Unity load (~9s) then identity→managers→C2. Dedicated gates on joiner early identity
    /// batch (uid + from_lobby + avatar|ping), with a loud 15s soft timeout.
    /// Per-peer: each LiteNetLib connection needs its own INIT unlock (room-global flag broke #3).
    /// </summary>
    private void BeginAwaitJoinerThenBootstrap(NetPeer peer, MatchRoom room, byte joinerActorNr)
    {
        if (!_peers.TryGetValue(peer, out var st))
            return;
        if (st.BootstrapSent)
            return;

        st.BootstrapPending = true;
        // Issue #28: keep IdentityAlreadyBootstrapped — a rejoin on the same live peer will not
        // repeat the identity batch, so the gate must not wait for it again.
        st.JoinerUidSeen = false;
        st.JoinerFromLobbySeen = false;
        st.JoinerAvatarSeen = false;
        st.JoinerPingSeen = false;

        // Issue #39: a reconnect takeover already proved who this is (stable token key) and
        // the client will NOT resend the identity batch for a match it thinks it is still in.
        // Waiting for it here left the player pre-init for 15 s — no world, no team, no pawn —
        // which is exactly «зашёл за команду и не появляется» from the report.
        if (st.ReconnectIdentityTrusted)
        {
            Console.WriteLine(
                $"[match-host] joiner={joinerActorNr} identity already trusted (reconnect takeover) " +
                "— bootstrap immediately, no identity wait");
            TrySendWorldBootstrap(peer, room, joinerActorNr, reason: "reconnect-trusted");
            return;
        }

        st.BootstrapTimeoutCts?.Cancel();
        st.BootstrapTimeoutCts = new CancellationTokenSource();
        var timeoutToken = st.BootstrapTimeoutCts.Token;

        Console.WriteLine(
            $"[match-host] await joiner identity before managers/C2 " +
            $"(joiner={joinerActorNr} need uid+from_lobby+(avatar|ping); " +
            $"softTimeout={JoinerReadySoftTimeout.TotalSeconds:0}s)");

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(JoinerReadySoftTimeout, timeoutToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            if (!_peers.TryGetValue(peer, out var late) || late.BootstrapSent || !late.BootstrapPending)
                return;
            Console.WriteLine(
                $"[match-host] WARN joiner-ready soft timeout {JoinerReadySoftTimeout.TotalSeconds:0}s — " +
                $"joiner={joinerActorNr} still missing early identity " +
                $"(uid={late.JoinerUidSeen} from_lobby={late.JoinerFromLobbySeen} " +
                $"avatar={late.JoinerAvatarSeen} ping={late.JoinerPingSeen}); " +
                "forcing host bootstrap (INIT unlock) anyway");
            TrySendWorldBootstrap(peer, room, joinerActorNr, reason: "soft-timeout");
        }, CancellationToken.None);
    }

    private void NoteJoinerIdentityProp(NetPeer peer, byte actorNr, string key)
    {
        if (!_peers.TryGetValue(peer, out var st) || !st.BootstrapPending || st.BootstrapSent)
            return;
        if (actorNr != st.ActorNr)
            return;

        switch (key)
        {
            case MatchRoomPropKeys.Uid:
                st.JoinerUidSeen = true;
                break;
            case MatchRoomPropKeys.FromLobby:
                st.JoinerFromLobbySeen = true;
                break;
            case MatchRoomPropKeys.Avatar:
                st.JoinerAvatarSeen = true;
                break;
            case MatchRoomPropKeys.Ping:
                st.JoinerPingSeen = true;
                break;
            case MatchRoomPropKeys.Verified:
                // Phones always author it inside the identity batch (uid→badgeId→verified→
                // from_lobby→avatar). It is informational — never part of the ready gate,
                // so an older client that omits it still unlocks INIT.
                break;
            default:
                return;
        }

        if (!st.IsJoinerIdentityReady || st.Room is null)
            return;

        Console.WriteLine(
            $"[match-host] joiner identity ready (uid+from_lobby+" +
            $"{(st.JoinerAvatarSeen ? "avatar" : "ping")}) — sending host bootstrap");
        TrySendWorldBootstrap(peer, st.Room, st.ActorNr, reason: "joiner-ready");
    }

    private void TrySendWorldBootstrap(NetPeer peer, MatchRoom room, byte joinerActorNr, string reason)
    {
        if (!_peers.TryGetValue(peer, out var st))
            return;

        // Idempotent per peer under _roomGate (soft-timeout Task vs RX identity race).
        lock (_roomGate)
        {
            if (st.BootstrapSent)
                return;
            st.BootstrapSent = true;
            st.BootstrapPending = false;
            // Issue #39: bootstrap only unlocks INIT — the scene is still being built, so world
            // relays to this peer wait until it proves the scene is up (MatchWorldGate).
            HoldWorldTrafficForNewScene(st);
            // Issue #28: identity props above are one-shot; latch them so a same-peer rejoin is
            // not stuck behind the INIT gate waiting for a batch the client will never resend.
            st.IdentityAlreadyBootstrapped = true;
        }
        try { st.BootstrapTimeoutCts?.Cancel(); }
        catch { /* ignore */ }
        st.BootstrapTimeoutCts = null;

        Console.WriteLine($"[match-host] TX world bootstrap trigger={reason} joiner={joinerActorNr}");
        SendWorldBootstrap(peer, room, joinerActorNr);
    }

    /// <summary>
    /// Post-Found bootstrap (codec-built, no capture replay) — <b>peer-local</b> INIT unlock.
    /// Order matches phone-host probe <c>20260722_003532_*</c> / <c>run-20260722_073504</c>
    /// (all ReliableOrdered + HasServerTime):
    /// host SIP nick → uid/badge/from_lobby/avatar/ping → managers×8 → C2=10 → money
    /// → (dedicated) Server Spectator → joiner money. Pawn only after inbound team=Tr/Ct.
    /// Called only after <see cref="BeginAwaitJoinerThenBootstrap"/> gate (or soft timeout).
    /// Late joiners also get a snapshot of living pawns already in the room.
    /// </summary>
    private void SendWorldBootstrap(NetPeer peer, MatchRoom room, byte joinerActorNr)
    {
        void SetHostProp(string key, LobbyVariant value)
        {
            SendAndDump(peer, MatchCodec.BuildSetProperty(
                NextServerTime(), MatchHostActor.ActorNr, key, value));
            lock (_roomGate)
                room.ActorProps[(MatchHostActor.ActorNr, key)] = value;
        }

        // 1) Host identity — phone TX actor1 only before managers (not joiner props).
        SendAndDump(peer, MatchCodec.BuildSetInternalProperty(
            NextServerTime(), MatchHostActor.ActorNr, MatchCodec.InternalPropNick,
            LobbyVariant.FromString(MatchHostActor.Name)));
        SetHostProp(MatchRoomPropKeys.Uid, LobbyVariant.FromString(MatchHostActor.BootstrapUid));
        SetHostProp(MatchRoomPropKeys.BadgeId, LobbyVariant.FromInt(0));
        // Phone host identity order is uid → badgeId → verified → from_lobby → avatar → ping
        // (gold 20261111_074258_049..053). Dedicated skipped verified, so the block was short.
        SetHostProp(MatchRoomPropKeys.Verified, LobbyVariant.FromBool(false));
        SetHostProp(MatchRoomPropKeys.FromLobby, LobbyVariant.FromBool(false));
        // Avatar must not abort managers/C2 — PlaceholderAvatarJpeg is fail-safe, but still guard.
        var avatarJpeg = MatchHostActor.PlaceholderAvatarJpeg;
        if (avatarJpeg is null || avatarJpeg.Length == 0)
        {
            Console.WriteLine("[match-host] WARN host avatar empty — TX empty ByteArray, continue bootstrap");
            avatarJpeg = [];
        }
        SetHostProp(MatchRoomPropKeys.Avatar, LobbyVariant.FromBytes(avatarJpeg));
        SetHostProp(MatchRoomPropKeys.Ping, LobbyVariant.FromInt(0));

        // 2) Scene managers (lens 25,34,36,86,83,24,23,23 — byte-matched to phone).
        // Duel gold: no BombManager / no RadarManager (Chat stays dedicated id=7).
        var duelBootstrap = IsDuelRoom(room);
        foreach (var mgr in MatchSceneManagers.Bootstrap)
        {
            if (duelBootstrap
                && (mgr.Id == MatchFlowTestParams.BombManagerObjectId
                    || mgr.Id == MatchSceneManagers.RadarManagerObjectId))
                continue;
            byte[]? trailing = null;
            if (mgr.CatalogIds is { } ids)
                trailing = MatchCodec.BuildDropCatalogPayload(ids);
            var pkt = MatchCodec.BuildCreateWorldObject(
                NextServerTime(),
                WorldObjectKind.SceneManager,
                mgr.Id,
                mgr.Name,
                mgr.FusTag,
                ownerActorNr: null,
                trailingPayload: trailing);
            SendAndDump(peer, pkt);
        }

        // 3) C2=FF0A — InitWaiting unlock (phone *_SetProperties_len14.bin).
        // Peer-local only: do NOT clobber room.RoomC2 if match-flow already advanced
        // (late / GIP join must keep live C2 for snapshots + other peers).
        // Duel gold C2=10 bag also carries current_loadout + null modifier (len≈114).
        if (duelBootstrap)
        {
            var duelWaitProps = BuildDuelWaitingPlayersRoomProps(room);
            SendAndDump(peer, MatchCodec.BuildSetProperties(
                NextServerTime(), actorNr: 0, duelWaitProps));
            lock (_roomGate)
            {
                foreach (var (k, v) in duelWaitProps)
                    room.ActorProps[(0, k)] = v;
                if (room.Flow.Phase == MatchFlowPhase.WaitingPlayers && room.RoomC2 < MatchC2States.WarmUp)
                    room.RoomC2 = MatchHostActor.C2AfterManagers;
            }
        }
        else
        {
            SendAndDump(peer, MatchCodec.BuildSetProperties(
                NextServerTime(),
                actorNr: 0,
                [(MatchRoomPropKeys.C2, LobbyVariant.FromByte(MatchHostActor.C2AfterManagers))]));
            lock (_roomGate)
            {
                if (room.Flow.Phase == MatchFlowPhase.WaitingPlayers && room.RoomC2 < MatchC2States.WarmUp)
                    room.RoomC2 = MatchHostActor.C2AfterManagers;
            }
        }

        // 4) Phone: host money after C2. Dedicated: then Server Spectator (no fighting pawn).
        SetHostProp(MatchRoomPropKeys.Money, LobbyVariant.FromInt(MatchFlowTestParams.BootstrapMoney));
        SetHostProp(
            MatchRoomPropKeys.Team,
            LobbyVariant.FromByte((byte)MatchTeam.Spectator));

        // 5) Joiner money — phone OBT later forces Ct; we await inbound team before pawn.
        // Issue #40: a reconnect keeps the balance the room already published. Bootstrapping
        // it back to 10000 mid-buy silently rewound the round economy for the returning peer.
        var joinerMoney = MatchFlowTestParams.BootstrapMoney;
        var reconnectTrusted = false;
        lock (_roomGate)
        {
            reconnectTrusted = _peers.TryGetValue(peer, out var jst) && jst.ReconnectIdentityTrusted;
            if (reconnectTrusted
                && room.ActorProps.TryGetValue((joinerActorNr, MatchRoomPropKeys.Money), out var money)
                && money.Kind == LobbyVariantKind.Int)
            {
                joinerMoney = money.Int;
            }
            else
            {
                room.ActorProps[(joinerActorNr, MatchRoomPropKeys.Money)] =
                    LobbyVariant.FromInt(joinerMoney);
            }
        }
        SendAndDump(peer, MatchCodec.BuildSetProperty(
            NextServerTime(), joinerActorNr, MatchRoomPropKeys.Money,
            LobbyVariant.FromInt(joinerMoney)));

        Console.WriteLine(
            $"[match-host] TX world bootstrap for joiner={joinerActorNr} " +
            $"(hostIdentity+avatar={avatarJpeg.Length}B " +
            $"managers={(duelBootstrap ? "Duel(no Bomb/Radar)" : MatchSceneManagers.Bootstrap.Length.ToString())} " +
            $"C2={MatchHostActor.C2AfterManagers} " +
            $"host={MatchHostActor.ActorNr}/'{MatchHostActor.Name}' team=Spectator; " +
            $"INIT unlocked — await team SetProperty then joiner CreateWorldObject echo)");

        // Mid-match / late join: C2=10 only clears InitWaiting. Push live room flow props +
        // actor identity so the joiner is not stuck on WaitingPlayers. Room/actor properties
        // are safe before the scene exists; world objects are not.
        // Issue #40: the aggregate CWO/Rpc snapshot is delivered exactly once when the world
        // gate opens (TryDeliverWorldSnapshot). Sending it here too duplicated pawns/entities
        // into a reconnecting client that had retained its scene, and pushed plant Rpc into a
        // scene that was still loading. A reconnect includes its own actor props (team/death/
        // money) because the client will not re-author the identity batch.
        SendMatchStateSnapshotToPeer(
            peer, room, excludeActorNr: reconnectTrusted ? (byte?)null : joinerActorNr);

        // Abrupt reconnect into same match: re-force previous Tr/Ct after INIT (spawn path).
        MaybeScheduleReconnectRestore(peer, room, joinerActorNr);
    }

    /// <summary>
    /// After INIT: restore the remembered fighting team (same SetProperty path as join) unless
    /// the client already picked one, then watch for a real <c>CreateWorldObject</c>.
    /// </summary>
    /// <remarks>
    /// Issue #39: this used to force <b>Spectator</b> after 5 s without a pawn. A phone that had
    /// just reconnected was still loading the world, so it lost its team and the player had to
    /// pick again in the buy/round-end phase. Now the host keeps the team and simply waits for
    /// the next C2=22/C2=31 spawn — the same rule as any mid-round joiner.
    /// </remarks>
    private void MaybeScheduleReconnectRestore(NetPeer peer, MatchRoom room, byte joinerActorNr)
    {
        if (!_peers.TryGetValue(peer, out var st))
            return;
        var team = st.PendingReconnectTeam;
        st.PendingReconnectTeam = null;
        if (team is not (MatchTeam.Tr or MatchTeam.Ct))
            return;

        // Issue #39: memory about «same player» must survive a new session token.
        var identityKey = st.IdentityKey ?? st.UserId;
        var actorNr = joinerActorNr;
        Console.WriteLine(
            $"[match-host] reconnect-restore: scheduled team={team} actor={actorNr} " +
            $"identity='{identityKey}' settle={ReconnectRestoreSettle.TotalMilliseconds:0}ms " +
            $"timeout={ReconnectRestoreTimeout.TotalSeconds:0}s (never auto-Spectator)");

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(ReconnectRestoreSettle, _cts.Token).ConfigureAwait(false);
                if (_cts.IsCancellationRequested)
                    return;
                if (!_peers.TryGetValue(peer, out var cur)
                    || cur.Room != room
                    || cur.ActorNr != actorNr)
                {
                    Console.WriteLine(
                        $"[match-host] reconnect-restore: ABORT actor={actorNr} — peer gone/replaced");
                    return;
                }

                // The client may have picked a team itself right after INIT (that is the normal
                // UI path). Never overwrite a fresh choice with the remembered one — that is
                // what made players «re-pick their team» after every reconnect.
                if (cur.TeamAuthoredThisSession)
                {
                    Console.WriteLine(
                        $"[match-host] reconnect-restore: actor={actorNr} уже выбрал команду сам " +
                        "— remembered team не навязываем");
                }
                else if (!TryForceTeam(peer, team.Value, out var msg))
                {
                    Console.WriteLine(
                        $"[match-host] reconnect-restore: force team FAILED actor={actorNr}: {msg}");
                }
                else
                {
                    Console.WriteLine(
                        $"[match-host] reconnect-restore: forced team={team} actor={actorNr} — " +
                        "await joiner CreateWorldObject (respawn)");
                }

                await Task.Delay(ReconnectRestoreTimeout, _cts.Token).ConfigureAwait(false);
                if (_cts.IsCancellationRequested)
                    return;
                if (!_peers.TryGetValue(peer, out cur)
                    || cur.Room != room
                    || cur.ActorNr != actorNr)
                {
                    Console.WriteLine(
                        $"[match-host] reconnect-restore: peer left before timeout actor={actorNr}");
                    ForgetReconnectMemory(identityKey);
                    return;
                }

                bool hasRespawned;
                MatchTeam liveTeam = MatchTeam.None;
                lock (_roomGate)
                {
                    // SpawnedPawns = a real CWO since this join. LivingPawns is the fallback for
                    // a mode that does not track SpawnedPawns. Both were cleared on takeover, so
                    // this cannot be satisfied by the dead player's old body (issue #39).
                    hasRespawned = room.SpawnedPawns.Contains(actorNr)
                        || room.LivingPawns.Values.Any(p => p.OwnerActorNr == actorNr);
                    if (room.ActorProps.TryGetValue((actorNr, MatchRoomPropKeys.Team), out var tv)
                        && tv.Kind == LobbyVariantKind.Byte)
                        liveTeam = (MatchTeam)tv.Byte;
                }

                if (hasRespawned && liveTeam is MatchTeam.Tr or MatchTeam.Ct)
                {
                    Console.WriteLine(
                        $"[match-host] reconnect-restore: OK actor={actorNr} team={liveTeam} " +
                        "respawned (CWO seen)");
                    // Issue #40: do not re-send the aggregate world snapshot here. The world
                    // gate already delivered it once when the scene was proven; live CWO/Rpc
                    // relays cover everything that changed after that. A second full snapshot
                    // duplicated pawns/entities into the retained client scene (ghosts).
                    ForgetReconnectMemory(identityKey);
                    return;
                }

                if (liveTeam == MatchTeam.Spectator)
                {
                    Console.WriteLine(
                        $"[match-host] reconnect-restore: actor={actorNr} сам ушёл в Spectator — ok");
                    ForgetReconnectMemory(identityKey);
                    return;
                }

                // Issue #39: no auto-Spectator. A reconnecting player is «dead for the round»
                // until the next C2=22 spawns him (same rule as a fresh mid-round joiner) —
                // forcing Spectator here threw people out of their team and made them re-pick.
                // The old body was already destroyed on takeover, so he is not counted alive.
                Console.WriteLine(
                    $"[match-host] reconnect-restore: no pawn yet actor={actorNr} " +
                    $"team={liveTeam} hasRespawned={hasRespawned} — team kept, no Spectator " +
                    "(spawn will come with the next C2=22/ C2=31)");
                // Memory stays: the player is still in the match and the next disconnect
                // re-adds it anyway.
            }
            catch (OperationCanceledException)
            {
                /* host dispose */
            }
            catch (Exception ex)
            {
                Console.WriteLine(
                    $"[match-host] reconnect-restore: error actor={actorNr}: {FormatException(ex)}");
            }
        });
    }

    /// <summary>Drop the «remembered team» for <paramref name="identityKey"/>.</summary>
    private void ForgetReconnectMemory(string? identityKey)
    {
        if (identityKey is not { Length: > 0 } key)
            return;
        lock (_roomGate)
            _reconnectTeams.Remove(key);
    }

    /// <summary>
    /// After INIT, TX current room match-flow props (C2/Time/Round/Score/bomberId/…) plus
    /// humans' actor props (team/avatar/money/…) so a late joiner syncs to the live match
    /// instead of staying on WaitingPlayers with nameless ghosts.
    /// </summary>
    /// <remarks>
    /// Issue #40: properties only. World objects (tracked entities, living pawns, plant Rpc)
    /// are delivered separately by <c>TryDeliverWorldSnapshot</c> once the peer's scene is
    /// proven, so a reconnect does not receive the same CWO twice. <paramref name="excludeActorNr"/>
    /// is null for a trusted reconnect: that client will not re-author its own identity batch,
    /// so its remembered team/death/money must be included.
    /// </remarks>
    private void SendMatchStateSnapshotToPeer(NetPeer peer, MatchRoom room, byte? excludeActorNr)
    {
        List<(string Key, LobbyVariant Value)> roomProps;
        List<(byte Actor, string Key, LobbyVariant Value)> actorProps;
        lock (_roomGate)
        {
            roomProps = room.ActorProps
                .Where(kv => kv.Key.Actor == 0)
                .Select(kv => (kv.Key.Key, kv.Value))
                .ToList();
            actorProps = room.ActorProps
                .Where(kv => kv.Key.Actor != 0
                             && (!excludeActorNr.HasValue || kv.Key.Actor != excludeActorNr.Value)
                             && kv.Key.Actor != MatchHostActor.ActorNr)
                .Select(kv => (kv.Key.Actor, kv.Key.Key, kv.Value))
                .ToList();
        }

        if (roomProps.Count > 0)
        {
            // Prefer a single SetProperties bag for room (actor 0) — same path as match-flow TX.
            var pkt = MatchCodec.BuildSetProperties(NextServerTime(), actorNr: 0, roomProps);
            SendAndDump(peer, pkt);
        }

        foreach (var (actor, key, value) in actorProps)
        {
            // Skip host props already sent in bootstrap identity block.
            SendAndDump(peer, MatchCodec.BuildSetProperty(NextServerTime(), actor, key, value));
        }

        Console.WriteLine(
            $"[match-host] TX match-state props to joiner peer " +
            $"(excludeActor={excludeActorNr?.ToString() ?? "-"} roomProps={roomProps.Count} " +
            $"actorProps={actorProps.Count} liveC2={room.RoomC2})");
    }

    /// <summary>
    /// Issue #28 — re-TX tracked round entities (weapon drops, grenades, planted-bomb props) to a
    /// rejoining peer. Values come from what this host actually relayed this round
    /// (<see cref="MatchRoom.TrackedRoundEntities"/>); nothing is invented. The plant Rpc itself
    /// is sent by <c>SyncBombPlantStateToPeer</c> right after this snapshot.
    /// </summary>
    private void SendTrackedRoundEntitySnapshotToPeer(NetPeer peer, MatchRoom room)
    {
        List<TrackedRoundEntity> entities;
        lock (_roomGate)
            entities = room.TrackedRoundEntities.Values.ToList();

        if (entities.Count == 0)
            return;

        // Rebuild each CWO from the stored decoded fields (kind, owner, fus tag and trailing
        // payload intact) so only the host header carries a fresh server time.
        foreach (var entity in entities)
        {
            var f = entity.Fields;
            SendAndDump(peer, MatchCodec.BuildCreateWorldObject(
                NextServerTime(),
                f.Kind,
                f.ObjectId,
                f.TypeName,
                f.FusTag,
                ownerActorNr: f.OwnerActorNr,
                trailingPayload: f.Trailing));
        }

        Console.WriteLine(
            $"[match-host] TX round-entity snapshot to joiner peer " +
            $"(entities={entities.Count} ids=[" +
            $"{string.Join(",", entities.Select(e => e.Fields.ObjectId))}])");
    }

    /// <summary>
    /// After INIT unlock, TX existing Entity pawns to a late joiner so they see peers already in-match.
    /// Bodies from decoded room state (same BuildCreateWorldObject / rpc=1 path as live echo).
    /// </summary>
    private void SendLivingPawnSnapshotToPeer(NetPeer peer, MatchRoom room, byte excludeOwnerActorNr)
    {
        List<MatchLivingPawn> pawns;
        lock (_roomGate)
        {
            pawns = room.LivingPawns.Values
                .Where(p => p.OwnerActorNr != excludeOwnerActorNr)
                .ToList();
        }
        if (pawns.Count == 0)
            return;

        foreach (var pawn in pawns)
        {
            var trailing = pawn.TrailingPayload.Length > 0
                ? pawn.TrailingPayload
                : MatchCodec.BuildSandstonePawnSpawnPayload(
                    pawn.TypeName == MatchSceneManagers.PlayerPawnNameCt
                        ? MatchTeam.Ct
                        : MatchTeam.Tr);
            var cwo = MatchCodec.BuildCreateWorldObject(
                NextServerTime(),
                WorldObjectKind.Entity,
                pawn.ObjectId,
                pawn.TypeName,
                pawn.FusTag,
                ownerActorNr: pawn.OwnerActorNr,
                trailingPayload: trailing);
            SendAndDump(peer, cwo);

            var stime = NextServerTime();
            var rpc1 = MatchCodec.BuildWorldObjectRpc(
                stime,
                pawn.ObjectId,
                rpcId: 1,
                gaaTarget: 4,
                field: 2,
                timeValue: stime / 1000.0,
                payload: MatchCodec.BuildPawnRpcSpawnTailPayload());
            SendAndDump(peer, rpc1);
        }

        Console.WriteLine(
            $"[match-host] TX living-pawn snapshot to joiner peer " +
            $"(excludeOwner={excludeOwnerActorNr} pawns={pawns.Count} " +
            $"ids=[{string.Join(",", pawns.Select(p => p.ObjectId))}] — once, no live CWO pre-INIT)");
    }
}

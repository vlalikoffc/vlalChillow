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

        var evt = MatchCodec.BuildActorJoinedEvent(
            NextServerTime(),
            new MatchGapActor
            {
                ActorNr = joinerActorNr,
                Name = joinerName,
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
        st.JoinerUidSeen = false;
        st.JoinerFromLobbySeen = false;
        st.JoinerAvatarSeen = false;
        st.JoinerPingSeen = false;
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
        SendAndDump(peer, MatchCodec.BuildSetProperties(
            NextServerTime(),
            actorNr: 0,
            [(MatchRoomPropKeys.C2, LobbyVariant.FromByte(MatchHostActor.C2AfterManagers))]));
        lock (_roomGate)
        {
            if (room.Flow.Phase == MatchFlowPhase.WaitingPlayers && room.RoomC2 < MatchC2States.WarmUp)
                room.RoomC2 = MatchHostActor.C2AfterManagers;
        }

        // 4) Phone: host money after C2. Dedicated: then Server Spectator (no fighting pawn).
        SetHostProp(MatchRoomPropKeys.Money, LobbyVariant.FromInt(MatchFlowTestParams.BootstrapMoney));
        SetHostProp(
            MatchRoomPropKeys.Team,
            LobbyVariant.FromByte((byte)MatchTeam.Spectator));

        // 5) Joiner money — phone OBT later forces Ct; we await inbound team before pawn.
        SendAndDump(peer, MatchCodec.BuildSetProperty(
            NextServerTime(), joinerActorNr, MatchRoomPropKeys.Money,
            LobbyVariant.FromInt(MatchFlowTestParams.BootstrapMoney)));
        lock (_roomGate)
            room.ActorProps[(joinerActorNr, MatchRoomPropKeys.Money)] =
                LobbyVariant.FromInt(MatchFlowTestParams.BootstrapMoney);

        Console.WriteLine(
            $"[match-host] TX world bootstrap for joiner={joinerActorNr} " +
            $"(hostIdentity+avatar={avatarJpeg.Length}B " +
            $"managers={(duelBootstrap ? "Duel(no Bomb/Radar)" : MatchSceneManagers.Bootstrap.Length.ToString())} " +
            $"C2={MatchHostActor.C2AfterManagers} " +
            $"host={MatchHostActor.ActorNr}/'{MatchHostActor.Name}' team=Spectator; " +
            $"INIT unlocked — await team SetProperty then joiner CreateWorldObject echo)");

        // Mid-match / late join: C2=10 only clears InitWaiting. Push live room flow props +
        // other actors' identity so joiner is not stuck on WaitingPlayers with empty world.
        SendMatchStateSnapshotToPeer(peer, room, excludeActorNr: joinerActorNr);
        SendLivingPawnSnapshotToPeer(peer, room, excludeOwnerActorNr: joinerActorNr);
        // If bomb already planted (C2=40), room props alone do not show the mesh — re-TX plant Rpc.
        SyncBombPlantStateToPeer(peer, room);

        // Abrupt reconnect into same match: re-force previous Tr/Ct after INIT (spawn path).
        MaybeScheduleReconnectRestore(peer, room, joinerActorNr);
    }

    /// <summary>
    /// After INIT: host-force remembered fighting team (same SetProperty team path as join),
    /// wait ~5s for a joiner CreateWorldObject (SpawnedPawns); on failure force Spectator once.
    /// Evidence: team prop + joiner CWO. Does not re-force Spectator if already done for this peer.
    /// </summary>
    private void MaybeScheduleReconnectRestore(NetPeer peer, MatchRoom room, byte joinerActorNr)
    {
        if (!_peers.TryGetValue(peer, out var st))
            return;
        var team = st.PendingReconnectTeam;
        if (team is not (MatchTeam.Tr or MatchTeam.Ct))
            return;
        if (st.ReconnectSpectatorFallbackDone)
        {
            st.PendingReconnectTeam = null;
            Console.WriteLine(
                $"[match-host] reconnect-restore: skip actor={joinerActorNr} — " +
                "spectator fallback already done once this peer session");
            return;
        }

        st.PendingReconnectTeam = null;
        var userId = st.UserId;
        var actorNr = joinerActorNr;
        Console.WriteLine(
            $"[match-host] reconnect-restore: scheduled team={team} actor={actorNr} " +
            $"userId='{userId}' settle={ReconnectRestoreSettle.TotalMilliseconds:0}ms " +
            $"timeout={ReconnectRestoreTimeout.TotalSeconds:0}s");

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

                if (!TryForceTeam(peer, team.Value, out var msg))
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
                    if (userId is { } uidLeft)
                    {
                        lock (_roomGate)
                            _reconnectTeams.Remove(uidLeft);
                    }
                    return;
                }

                // Already forced Spectator once this session — never again.
                if (cur.ReconnectSpectatorFallbackDone)
                {
                    Console.WriteLine(
                        $"[match-host] reconnect-restore: skip Spectator actor={actorNr} — once only");
                    if (userId is { } uidOnce)
                    {
                        lock (_roomGate)
                            _reconnectTeams.Remove(uidOnce);
                    }
                    return;
                }

                bool hasRespawned;
                MatchTeam liveTeam = MatchTeam.None;
                lock (_roomGate)
                {
                    // SpawnedPawns = ever CWO'd after reconnect (survives combat death).
                    // LivingPawns alone would mis-spectate a player who spawned then died.
                    hasRespawned = room.SpawnedPawns.Contains(actorNr)
                        || room.LivingPawns.Values.Any(p => p.OwnerActorNr == actorNr);
                    if (room.ActorProps.TryGetValue((actorNr, MatchRoomPropKeys.Team), out var tv)
                        && tv.Kind == LobbyVariantKind.Byte)
                        liveTeam = (MatchTeam)tv.Byte;
                }

                if (hasRespawned && liveTeam == team)
                {
                    Console.WriteLine(
                        $"[match-host] reconnect-restore: OK actor={actorNr} team={liveTeam} " +
                        "respawned (CWO seen)");
                    if (userId is { } uidOk)
                    {
                        lock (_roomGate)
                            _reconnectTeams.Remove(uidOk);
                    }
                    return;
                }

                // If they already moved themselves to Spectator, count as done — do not re-force.
                if (liveTeam == MatchTeam.Spectator)
                {
                    cur.ReconnectSpectatorFallbackDone = true;
                    Console.WriteLine(
                        $"[match-host] reconnect-restore: actor={actorNr} already Spectator — once done");
                    if (userId is { } uidSpec)
                    {
                        lock (_roomGate)
                            _reconnectTeams.Remove(uidSpec);
                    }
                    return;
                }

                Console.WriteLine(
                    $"[match-host] reconnect-restore: FAILED actor={actorNr} " +
                    $"team={liveTeam} hasRespawned={hasRespawned} → Spectator fallback (once)");
                cur.ReconnectSpectatorFallbackDone = true;
                if (TryForceTeam(peer, MatchTeam.Spectator, out var specMsg))
                    Console.WriteLine(
                        $"[match-host] reconnect-restore: Spectator forced actor={actorNr} ({specMsg})");
                else
                    Console.WriteLine(
                        $"[match-host] reconnect-restore: Spectator force failed actor={actorNr}: {specMsg}");
                if (userId is { } uidFail)
                {
                    lock (_roomGate)
                        _reconnectTeams.Remove(uidFail);
                }
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

    /// <summary>
    /// After INIT, TX current room match-flow props (C2/Time/Round/Score/bomberId/…) plus
    /// other humans' actor props (team/avatar/money/…) so a GIP late joiner syncs to the
    /// live match instead of staying on WaitingPlayers with nameless ghosts.
    /// </summary>
    private void SendMatchStateSnapshotToPeer(NetPeer peer, MatchRoom room, byte excludeActorNr)
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
                             && kv.Key.Actor != excludeActorNr
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
            $"[match-host] TX match-state snapshot to joiner peer " +
            $"(excludeActor={excludeActorNr} roomProps={roomProps.Count} " +
            $"actorProps={actorProps.Count} liveC2={room.RoomC2})");
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

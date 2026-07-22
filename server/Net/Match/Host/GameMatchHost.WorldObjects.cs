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
    /// <summary>
    /// Joiner CreateWorldObject (Entity pawn): phone allocates its own netId (capture len=59
    /// used id=257, name=Tr_Tr, fusTag=101) — host must echo HasServerTime, then rpc=1 + state
    /// like phone post-create. SceneManager creates from clients stay ignored.
    /// </summary>
    private void HandleCreateWorldObject(NetPeer peer, MatchFrameFlags flags, byte[] raw)
    {
        DumpCapture("match_rx_CreateWorldObject", raw);
        try
        {
            if (!MatchCodec.TryOpenBody(raw, out _, out _, out _, out var bodyBytes))
            {
                Console.WriteLine("[match-host] CreateWorldObject — could not open body");
                return;
            }

            var parsed = MatchCodec.ParseCreateWorldObjectBody(new LobbyReader(bodyBytes));
            Console.WriteLine(
                $"[match-host] CreateWorldObject kind={parsed.Kind} id={parsed.ObjectId} " +
                $"owner={parsed.OwnerActorNr?.ToString() ?? "-"} name='{parsed.TypeName}' " +
                $"fusTag={parsed.FusTag} trailLen={parsed.Trailing.Length}");

            if (!_peers.TryGetValue(peer, out var st) || st.Room is null)
                return;
            var room = st.Room;

            if (parsed.Kind != WorldObjectKind.Entity
                || parsed.TypeName is not (
                    MatchSceneManagers.PlayerPawnNameTr or MatchSceneManagers.PlayerPawnNameCt))
            {
                Console.WriteLine(
                    $"[match-host] ignore non-pawn CreateWorldObject name='{parsed.TypeName}' " +
                    "(scene managers are host-authored)");
                return;
            }

            // Rebuild trailing from decoded pose (or fall back to Sandstone for team).
            byte[] trailing;
            float px, py, pz;
            if (MatchCodec.TryParsePawnSpawnTrailing(
                    parsed.Trailing, out px, out py, out pz,
                    out var qx, out var qy, out var qz, out var qw))
            {
                trailing = MatchCodec.BuildPawnSpawnPayload(px, py, pz, qx, qy, qz, qw);
            }
            else
            {
                var team = parsed.TypeName == MatchSceneManagers.PlayerPawnNameCt
                    ? MatchTeam.Ct
                    : MatchTeam.Tr;
                trailing = MatchCodec.BuildSandstonePawnSpawnPayload(team);
                var p = team == MatchTeam.Ct
                    ? MatchSceneManagers.PawnSpawn.SandstonePosCt
                    : MatchSceneManagers.PawnSpawn.SandstonePosTr;
                px = p.X;
                py = p.Y;
                pz = p.Z;
                Console.WriteLine(
                    $"[match-host] pawn trailing len={parsed.Trailing.Length} not 45 — " +
                    "using Sandstone pose");
            }

            var owner = parsed.OwnerActorNr ?? st.ActorNr;
            List<short> staleIds;
            lock (_roomGate)
            {
                // Respawn without Destroy (or race): prior living pawns for this owner become
                // radar/occlusion ghosts through walls if peers never get Destroy.
                staleIds = room.LivingPawns
                    .Where(kv => kv.Value.OwnerActorNr == owner && kv.Key != parsed.ObjectId)
                    .Select(kv => kv.Key)
                    .ToList();
                foreach (var oldId in staleIds)
                    room.LivingPawns.Remove(oldId);

                room.SpawnedPawns.Add(owner);
                // Respawn (prep / between-round pawn): cancel combat-death grace and clear any
                // stale dead flag so the fighter counts as alive for wipe / bomber picks.
                room.Flow.PendingCombatDestroy.Remove(owner);
                room.Flow.DeadActors.Remove(owner);
                if (room.ActorProps.TryGetValue((owner, MatchRoomPropKeys.Death), out var deadProp)
                    && !(deadProp.Kind == LobbyVariantKind.Int && deadProp.Int == 0))
                {
                    room.ActorProps[(owner, MatchRoomPropKeys.Death)] = LobbyVariant.FromInt(0);
                }
                room.LivingPawns[parsed.ObjectId] = new MatchLivingPawn
                {
                    ObjectId = parsed.ObjectId,
                    OwnerActorNr = owner,
                    TypeName = parsed.TypeName,
                    FusTag = parsed.FusTag,
                    TrailingPayload = trailing,
                    PosX = px,
                    PosY = py,
                    PosZ = pz,
                };
            }

            // Heal peers before new CWO: TX Destroy for stale ids (phone order Destroy→Create).
            if (staleIds.Count > 0)
            {
                Console.WriteLine(
                    $"[match-host] LivingPawns replace owner={owner} stale ids=" +
                    $"[{string.Join(",", staleIds)}] → TX Destroy then id={parsed.ObjectId}");
                foreach (var oldId in staleIds)
                {
                    var destroyPkt = MatchCodec.BuildDestroyWorldObject(NextServerTime(), oldId);
                    var nD = BroadcastInitReadyExcept(
                        room, peer, destroyPkt, tag: "match_tx_DestroyWorldObject");
                    Console.WriteLine(
                        $"[match-host] TX relay DestroyWorldObject id={oldId} → peers={nD} " +
                        "(stale-pawn heal)");
                }
            }

            var echo = MatchCodec.BuildCreateWorldObject(
                NextServerTime(),
                parsed.Kind,
                parsed.ObjectId,
                parsed.TypeName,
                parsed.FusTag,
                ownerActorNr: owner,
                trailingPayload: trailing);
            // INIT-ready peers only: late joiners still loading get this pawn via snapshot
            // at their own C2 unlock — avoids double CWO (live broadcast + snapshot) ghost.
            // Exclude sender: owner already created locally; echo back doubles entities.
            var nEcho = BroadcastInitReadyExcept(room, peer, echo, tag: "match_tx_CreateWorldObject");
            Console.WriteLine(
                $"[match-host] TX relay CreateWorldObject id={parsed.ObjectId} " +
                $"pawn='{parsed.TypeName}' owner={owner} " +
                $"→ peers={nEcho} len={echo.Length}");

            // Phone after CWO: WorldObjectRpc rpc=1 gaa=AllCachedViaServer(4) field=2 + spawn tail.
            var stime = NextServerTime();
            var rpc1 = MatchCodec.BuildWorldObjectRpc(
                stime,
                parsed.ObjectId,
                rpcId: 1,
                gaaTarget: 4,
                field: 2,
                timeValue: stime / 1000.0,
                payload: MatchCodec.BuildPawnRpcSpawnTailPayload());
            var nRpc = BroadcastInitReady(room, rpc1, tag: "match_tx_WorldObjectRpc");
            Console.WriteLine(
                $"[match-host] TX relay WorldObjectRpc rpc=1 id={parsed.ObjectId} → peers={nRpc}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[match-host] CreateWorldObject handle failed: {FormatException(ex)}");
        }
    }

    private void HandleWorldObjectRpc(NetPeer peer, MatchFrameFlags flags, byte[] raw, bool logRpc = true)
    {
        if (logRpc)
            DumpCapture("match_rx_WorldObjectRpc", raw);
        try
        {
            if (!MatchCodec.TryOpenBody(raw, out _, out _, out _, out var bodyBytes))
            {
                Console.WriteLine("[match-host] WorldObjectRpc — could not open body");
                return;
            }

            var parsed = MatchCodec.ParseWorldObjectRpcBody(new LobbyReader(bodyBytes));
            if (logRpc)
            {
                Console.WriteLine(
                    $"[match-host] WorldObjectRpc id={parsed.ObjectId} rpc={parsed.RpcId} " +
                    $"gaa={parsed.GaaTarget} field={parsed.Field} payloadLen={parsed.Payload.Length}");
            }

            if (!_peers.TryGetValue(peer, out var st) || st.Room is null)
                return;

            // Echo with HasServerTime (client TX is flags=None).
            // gaa AllCached(2) / Others(1) / All(0): client already executeImmediate — do NOT
            // echo back to sender (WeaponDropManager field 4/5 ping-pong flood,
            // run-20260722_102505). gaa *ViaServer(3/4): client waits for host — include sender.
            var stime = NextServerTime();
            var echo = MatchCodec.BuildWorldObjectRpc(
                stime,
                parsed.ObjectId,
                parsed.RpcId,
                parsed.GaaTarget,
                parsed.Field,
                parsed.TimeValue,
                parsed.Payload.Length > 0 ? parsed.Payload : null);
            NetPeer? exceptSender = parsed.GaaTarget is 3 or 4 ? null : peer;
            var n = BroadcastInitReadyExcept(
                st.Room, exceptSender, echo, tag: "match_tx_WorldObjectRpc", dumpCapture: logRpc);
            if (logRpc)
            {
                Console.WriteLine(
                    $"[match-host] TX relay WorldObjectRpc id={parsed.ObjectId} rpc={parsed.RpcId} " +
                    $"→ peers={n} exceptSender={(exceptSender is null ? "no(ViaServer)" : "yes")}");
            }

            // BombManager (scene id=8): Rpc(1)/Rpc(2) = plant pose (nyo/nyu). Host drives C2=40.
            if (parsed.ObjectId == MatchFlowTestParams.BombManagerObjectId
                && parsed.RpcId is 1 or 2)
            {
                TryEnterBombPlanted(st.Room, sourceRpc: parsed.RpcId);
            }
            // Rpc(6)=nzu(bool,byte,float): bool true → PlantedBombController.vxr (defuse FX);
            // false → vxq (explode FX). End round immediately — do not wait fuse timeout.
            else if (parsed.ObjectId == MatchFlowTestParams.BombManagerObjectId
                     && parsed.RpcId == 6)
            {
                HandleBombManagerNzu(st.Room, parsed.Payload);
            }
            else if (parsed.ObjectId == MatchFlowTestParams.BombManagerObjectId
                     && parsed.RpcId == 5)
            {
                // Rpc(5)=nzg — defuse-related helper; do not invent round-end from it alone.
                Console.WriteLine(
                    $"[match-host] match-flow: BombManager rpc=5 " +
                    $"(nzg — logged only, payloadLen={parsed.Payload.Length})");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[match-host] WorldObjectRpc handle failed: {FormatException(ex)}");
        }
    }

    /// <summary>
    /// DestroyWorldObject (<c>fuo=201</c> / <c>fuu</c>): client body = i16 id (capture len=4).
    /// Must relay HasServerTime to other INIT-ready peers — without this, drops/shields/old
    /// pawns stack on peers (weapon multiply, eternal spawn shield, radar ghosts through walls).
    /// </summary>
    private void HandleDestroyWorldObject(NetPeer peer, MatchFrameFlags flags, byte[] raw)
    {
        DumpCapture("match_rx_DestroyWorldObject", raw);
        try
        {
            if (!MatchCodec.TryOpenBody(raw, out _, out _, out _, out var bodyBytes))
            {
                Console.WriteLine("[match-host] DestroyWorldObject — could not open body");
                return;
            }

            var objectId = MatchCodec.ParseDestroyWorldObjectBody(new LobbyReader(bodyBytes));
            if (!_peers.TryGetValue(peer, out var st) || st.Room is null)
                return;
            var room = st.Room;

            var removed = false;
            byte? ownerNr = null;
            lock (_roomGate)
            {
                if (room.LivingPawns.TryGetValue(objectId, out var pawn))
                {
                    ownerNr = pawn.OwnerActorNr;
                    removed = room.LivingPawns.Remove(objectId);
                }
            }

            var echo = MatchCodec.BuildDestroyWorldObject(NextServerTime(), objectId);
            var n = BroadcastInitReadyExcept(room, peer, echo, tag: "match_tx_DestroyWorldObject");
            Console.WriteLine(
                $"[match-host] TX relay DestroyWorldObject id={objectId} → peers={n} " +
                $"unregistered={(removed ? "LivingPawn" : "none")}");

            if (ownerNr is { } owner)
                NotePawnDestroyed(room, owner);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[match-host] DestroyWorldObject handle failed: {FormatException(ex)}");
        }
    }

    /// <summary>
    /// LiteNetLib latency tick → actor prop <c>ping</c> = <see cref="NetPeer.RoundTripTime"/>.
    /// Same key/type as client SetProperty ping (int); value is server-measured.
    /// </summary>
    private void OnPeerLatencyUpdate(NetPeer peer, int latency)
    {
        if (!_peers.TryGetValue(peer, out var st) || st.Room is null || st.ActorNr == 0)
            return;
        if (!st.BootstrapSent)
            return;

        var rtt = Math.Max(0, peer.RoundTripTime);
        var room = st.Room;
        var actorNr = st.ActorNr;
        lock (_roomGate)
        {
            if (room.ActorProps.TryGetValue((actorNr, MatchRoomPropKeys.Ping), out var prev)
                && prev.Kind == LobbyVariantKind.Int
                && prev.Int == rtt)
                return;
            room.ActorProps[(actorNr, MatchRoomPropKeys.Ping)] = LobbyVariant.FromInt(rtt);
        }

        var pkt = MatchCodec.BuildSetProperty(
            NextServerTime(), actorNr, MatchRoomPropKeys.Ping, LobbyVariant.FromInt(rtt));
        BroadcastRoom(room, pkt, tag: "match_tx_SetProperty");
        Console.WriteLine(
            $"[match-host] ping from RTT actor={actorNr} rtt={rtt}ms (LNL latency={latency})");
    }

    private void HandleWorldObjectStateRelay(NetPeer peer, byte[] raw)
    {
        // Owner fat State after CWO — relay as-is (flags=None) to other INIT-ready peers.
        // Prefer this over host-invented thin standing ticks (phone does relay, not invent).
        if (!_peers.TryGetValue(peer, out var st) || st.Room is null)
            return;

        short? objectId = null;
        if (raw.Length >= 4
            && MatchCodec.TryParseEnvelope(raw, out _, out var op, out _, out var bodyOffset)
            && op == MatchOpcode.WorldObjectState
            && raw.Length >= bodyOffset + 2)
        {
            objectId = BinaryPrimitives.ReadInt16LittleEndian(raw.AsSpan(bodyOffset, 2));
            lock (_roomGate)
            {
                if (st.Room.LivingPawns.TryGetValue(objectId.Value, out var pawn))
                    pawn.OwnerSendsState = true;
            }
        }

        var n = BroadcastInitReadyUnreliableExcept(st.Room, peer, raw);
        var logged = Interlocked.Increment(ref _stateRelayLogged);
        if (logged <= StateLogFirst || logged % StateLogEveryK == 0)
        {
            Console.WriteLine(
                $"[match-host] TX relay WorldObjectState id={objectId?.ToString() ?? "?"} " +
                $"len={raw.Length} → peers={n}");
        }
    }

    private void TickLivingPawnStates()
    {
        List<(MatchRoom Room, MatchLivingPawn Pawn)> snapshot;
        lock (_roomGate)
        {
            // Skip pawns whose owner already streams fat State — relay handles those.
            snapshot = _roomsByPassword.Values
                .SelectMany(r => r.LivingPawns.Values
                    .Where(p => !p.OwnerSendsState)
                    .Select(p => (r, p)))
                .ToList();
            if (snapshot.Count == 0)
                return;
            foreach (var (_, pawn) in snapshot)
            {
                pawn.Seq++;
                pawn.TickTime += 0.05f;
            }
        }

        foreach (var (room, pawn) in snapshot)
        {
            var pkt = MatchCodec.BuildWorldObjectStateStanding(
                pawn.ObjectId, pawn.Seq, pawn.TickTime, pawn.PosX, pawn.PosY, pawn.PosZ);
            var n = BroadcastInitReadyUnreliable(room, pkt, tag: pawn.StateCaptures < 4
                ? "match_tx_WorldObjectState"
                : null);
            if (pawn.StateCaptures < 4)
                pawn.StateCaptures++;
            if (pawn.Seq == 1 || pawn.Seq % 40 == 0)
            {
                Console.WriteLine(
                    $"[match-host] TX WorldObjectState Unreliable id={pawn.ObjectId} " +
                    $"seq={pawn.Seq} len={pkt.Length} → peers={n} " +
                    "(host standing — no owner State yet)");
            }
        }
    }

}

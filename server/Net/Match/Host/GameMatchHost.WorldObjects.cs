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

            // Scene managers are host-authored only (bootstrap catalog ids 1–8).
            if (parsed.Kind == WorldObjectKind.SceneManager
                || IsBootstrapSceneManagerName(parsed.TypeName))
            {
                Console.WriteLine(
                    $"[match-host] ignore CreateWorldObject name='{parsed.TypeName}' " +
                    "(scene managers are host-authored)");
                return;
            }

            // Non-pawn Entity (weapon drops / planted bomb / etc.): relay + track for round cleanup.
            if (parsed.Kind != WorldObjectKind.Entity
                || parsed.TypeName is not (
                    MatchSceneManagers.PlayerPawnNameTr or MatchSceneManagers.PlayerPawnNameCt))
            {
                HandleTrackedRoundEntityCreate(peer, room, parsed);
                return;
            }

            // Pawn CWO trailing = client-authored spawn pose (44B) + a length-prefixed loadout tag
            // (character/skin — e.g. "AgentCTLincoln" in a 59B Ct trailing; empty string for a
            // default 45B trailing). The OWNER authors its own pawn, so we RELAY the trailing
            // VERBATIM. The old code rebuilt a canonical 45B pose via BuildPawnSpawnPayload, which
            // silently dropped everything past the pose block — so any buy-menu weapon / character
            // pick was lost and the pawn spawned with the host's default ("random") loadout
            // (TDM free-buy bug). Only synthesize a Sandstone pose when the client trailing is too
            // short to even read a pose (never observed for a real pawn spawn).
            byte[] trailing;
            float px, py, pz;
            if (MatchCodec.TryParsePawnSpawnTrailing(
                    parsed.Trailing, out px, out py, out pz,
                    out _, out _, out _, out _))
            {
                trailing = parsed.Trailing; // verbatim — preserves buy/loadout/character tag
                var loadoutTag = MatchCodec.TryReadPawnLoadoutTag(parsed.Trailing, out var tag)
                    ? tag
                    : null;
                Console.WriteLine(
                    $"[tdm-buy] pawn CWO owner={parsed.OwnerActorNr?.ToString() ?? "-"} " +
                    $"name='{parsed.TypeName}' trailLen={parsed.Trailing.Length} " +
                    (string.IsNullOrEmpty(loadoutTag)
                        ? "loadout=default (no tag) — relayed verbatim (was rebuilt→dropped before)"
                        : $"loadout='{loadoutTag}' — relayed verbatim (client buy/character honored)"));
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
                    $"[match-host] pawn trailing len={parsed.Trailing.Length} < 45 — " +
                    "using Sandstone pose (cannot preserve loadout tag)");
            }

            var owner = parsed.OwnerActorNr ?? st.ActorNr;
            List<short> staleIds;
            var clearedDead = false;
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
                // OBSERVE before relay: respawn cancels combat-death grace and clears DeadActors
                // so the fighter counts alive for wipe / bomber picks (authority, not invent).
                room.Flow.PendingCombatDestroy.Remove(owner);
                clearedDead = room.Flow.DeadActors.Remove(owner);
                // Ranked/Allies: death is a per-round alive flag → reset to 0 on respawn so the
                // fighter counts alive. DeathMatch/TDM: death is a CUMULATIVE deaths counter
                // (phone gold increments 1,2,3… and never resets on respawn) — leave it so the
                // next master-authored death produces a real change the client can detect.
                if (!IsDeathMatchRoom(room)
                    && room.ActorProps.TryGetValue((owner, MatchRoomPropKeys.Death), out var deadProp)
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

            if (clearedDead)
            {
                Console.WriteLine(
                    $"[observe] pawn CWO respawn owner={owner} id={parsed.ObjectId} " +
                    "(DeadActors cleared — alive for wipe)");
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

            var dropRelay = false;

            // ChatManager mid-match text: length-prefixed UTF-8 payload (captures
            // 20260723_030750_1117 / 20260723_030756_1126). Lobby OpChat never sees these —
            // execute slash commands (/set start, …) via host callback when present.
            // Slash: do NOT fan-out the command text to other peers (private Server feedback).
            if (parsed.ObjectId == MatchSceneManagers.ChatManagerObjectId
                && TryDecodeChatManagerText(parsed.Payload, out var chatText))
            {
                Console.WriteLine(
                    $"[match-host] ChatManager text actor={st.ActorNr}: {chatText}");
                if (chatText.StartsWith('/'))
                {
                    dropRelay = true;
                    if (peer.Address is { } chatIp)
                    {
                        try { OnMatchChatSlashCommand?.Invoke(chatIp, chatText); }
                        catch (Exception ex)
                        {
                            Console.WriteLine(
                                $"[match-host] ChatManager slash handler failed: {ex.Message}");
                        }
                    }
                }
            }

            // Observe-then-forward (MATCH_ARCHITECTURE): sync decode + authority update BEFORE
            // relay. Never Task.Delay; never relay-only blind for bomb/combat. Peer↔peer
            // exceptSender still updates DeadActors / fuse / wipe same tick. [observe] logs
            // only on decisive events (plant/defuse/explode) — damage attribution logs inside
            // NoteDeathMatchDamage on accept/ignore, not every hit here.
            var alliesRoom = IsAlliesRoom(st.Room);
            // Allies gold plant = field=3; field=1 near C2=22/31 is round-start reset (relay only).
            // Ranked/Escalation carry plant = field=1/2.
            var isAlliesPlantField = alliesRoom
                && parsed.Field == MatchFlowTestParams.BombManagerFieldEscalationAutoPlant;
            var isRankedPlantField = !alliesRoom
                && parsed.Field is MatchFlowTestParams.BombManagerFieldPlantNyo
                    or MatchFlowTestParams.BombManagerFieldPlantNyu;
            if (parsed.ObjectId == MatchFlowTestParams.BombManagerObjectId
                && (isAlliesPlantField || isRankedPlantField))
            {
                MatchFlowPhase plantPhase;
                bool alreadyPlanted;
                lock (_roomGate)
                {
                    plantPhase = st.Room.Flow.Phase;
                    alreadyPlanted = st.Room.Flow.BombPlanted
                        || st.Room.Flow.PendingBombPlant
                        || st.Room.Flow.Phase == MatchFlowPhase.BombPlanted
                        || st.Room.Flow.Phase is MatchFlowPhase.RoundEndPause
                            or MatchFlowPhase.MatchOver;
                }
                if (alreadyPlanted)
                {
                    Console.WriteLine(
                        $"[observe] BombManager plant field={parsed.Field} " +
                        "IGNORED — already planted this round (one bomb/round; no relay)");
                    dropRelay = true;
                }
                else if (!IsAlliesPlantablePhase(st.Room, plantPhase))
                {
                    // Allies: PurchasePhase (C2=31) only. Ranked: RoundLive.
                    // Early equip/reset — relay pose, do not invent C2=40.
                    Console.WriteLine(
                        $"[observe] BombManager plant field={parsed.Field} " +
                        $"from actor={st.ActorNr} not-authority phase={plantPhase} " +
                        "(not plantable — relay only)");
                    dropRelay = false;
                }
                else
                {
                    Console.WriteLine(
                        $"[observe] BombManager plant field={parsed.Field} " +
                        $"from actor={st.ActorNr} phase={plantPhase} " +
                        "(apply fuse + host fan-out + relay)");
                    var payloadCopy = parsed.Payload.Length > 0
                        ? (byte[])parsed.Payload.Clone()
                        : null;
                    var fanOutPeers = TryEnterBombPlanted(
                        st.Room,
                        sourceField: (byte)parsed.Field,
                        plantPayload: payloadCopy,
                        plantTimeValue: parsed.TimeValue,
                        rpcId: parsed.RpcId,
                        gaaTarget: parsed.GaaTarget,
                        planterActorNr: st.ActorNr);
                    lock (_roomGate)
                    {
                        if (!st.Room.Flow.BombPlanted
                            || st.Room.Flow.BombPlantedUtc == DateTime.MinValue)
                        {
                            dropRelay = true;
                        }
                        else if (alliesRoom)
                        {
                            // Always exceptSender-relay in addition to fan-out — peers often
                            // miss host-rebuilt Rpc; planter already applied locally.
                            var nReady = CountInitReadyPeers(st.Room);
                            Console.WriteLine(
                                $"[observe] Allies plant fan-out peers={fanOutPeers} " +
                                $"initReady={nReady} — keep exceptSender relay");
                            dropRelay = false;
                        }
                    }
                }
            }
            else if (alliesRoom
                     && parsed.ObjectId == MatchFlowTestParams.BombManagerObjectId
                     && parsed.Field is MatchFlowTestParams.BombManagerFieldPlantNyo
                         or MatchFlowTestParams.BombManagerFieldPlantNyu)
            {
                // Gold field=1 near C2=22/31 = round-start reset — relay only, never C2=40.
                Console.WriteLine(
                    $"[observe] Allies BombManager field={parsed.Field} " +
                    $"from actor={st.ActorNr} (reset/equip — relay only, not plant)");
            }
            else if (parsed.ObjectId == MatchFlowTestParams.BombManagerObjectId
                     && parsed.Field == MatchFlowTestParams.BombManagerFieldNzu)
            {
                Console.WriteLine(
                    $"[observe] BombManager field=6 nzu from actor={st.ActorNr} " +
                    $"payloadLen={parsed.Payload.Length} (defuse/explode — apply before relay)");
                HandleBombManagerNzu(st.Room, parsed.Payload, senderActorNr: st.ActorNr);
            }
            else if (parsed.ObjectId == MatchFlowTestParams.BombManagerObjectId
                     && parsed.Field == MatchFlowTestParams.BombManagerFieldNzg)
            {
                Console.WriteLine(
                    $"[observe] BombManager field=5 nzg from actor={st.ActorNr} " +
                    $"(logged only, payloadLen={parsed.Payload.Length})");
            }
            else if (parsed.Field == DeathMatchFlowParams.PawnDamageRpcField
                     && parsed.ObjectId != MatchFlowTestParams.BombManagerObjectId)
            {
                // Attribution only for living fighter pawns — exclude WeaponDropManager (id=4)
                // and other scene managers that also use field=5.
                bool isLivingPawn;
                lock (_roomGate)
                    isLivingPawn = st.Room.LivingPawns.ContainsKey(parsed.ObjectId);
                if (isLivingPawn)
                    NoteDeathMatchDamage(
                        st.Room,
                        attackerActorNr: st.ActorNr,
                        victimPawnId: parsed.ObjectId,
                        damagePayload: parsed.Payload);
            }

            if (dropRelay)
                return;

            // Echo with HasServerTime (client TX is flags=None).
            // gaa AllCached(2) / Others(1) / All(0): client already executeImmediate — do NOT
            // echo back to sender (WeaponDropManager field 4/5 ping-pong flood,
            // run-20260722_102505). gaa *ViaServer(3/4): client waits for host — include sender.
            // ChatManager (id=7): always fan-out to every other INIT-ready peer (never drop a
            // team side). ViaServer includes sender; Others excludes only the speaker.
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

            // OBSERVE first: drop LivingPawns / track combat-death grace, then relay promptly.
            var removed = false;
            var removedTracked = false;
            byte? ownerNr = null;
            lock (_roomGate)
            {
                if (room.LivingPawns.TryGetValue(objectId, out var pawn))
                {
                    ownerNr = pawn.OwnerActorNr;
                    removed = room.LivingPawns.Remove(objectId);
                }
                removedTracked = room.TrackedRoundEntities.Remove(objectId);
            }

            if (ownerNr is { } owner)
            {
                Console.WriteLine(
                    $"[observe] DestroyWorldObject id={objectId} owner={owner} " +
                    "(LivingPawn remove → combat-death grace before relay)");
                NotePawnDestroyed(room, owner);
            }

            var echo = MatchCodec.BuildDestroyWorldObject(NextServerTime(), objectId);
            var n = BroadcastInitReadyExcept(room, peer, echo, tag: "match_tx_DestroyWorldObject");
            var unreg = removed ? "LivingPawn" : removedTracked ? "TrackedRoundEntity" : "none";
            Console.WriteLine(
                $"[match-host] TX relay DestroyWorldObject id={objectId} → peers={n} " +
                $"unregistered={unreg}");
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
        // Pose/State is not combat authority: alive/HP lethality comes from death/kills props
        // + LivingPawns (see observe-then-forward). Mark OwnerSendsState only — no invent HP.
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

    private static bool IsBootstrapSceneManagerName(string typeName)
    {
        foreach (var e in MatchSceneManagers.Bootstrap)
        {
            if (string.Equals(e.Name, typeName, StringComparison.Ordinal))
                return true;
        }
        return false;
    }

    /// <summary>
    /// Relay + track a non-pawn Entity CreateWorldObject (drops / planted bomb entities).
    /// Destroyed on RoundEnd / PreStart via <see cref="DestroyTrackedRoundEntities"/>.
    /// </summary>
    private void HandleTrackedRoundEntityCreate(
        NetPeer peer, MatchRoom room, MatchCodec.ParsedCreateWorldObject parsed)
    {
        lock (_roomGate)
            room.TrackedRoundEntities[parsed.ObjectId] = parsed.TypeName;

        var echo = MatchCodec.BuildCreateWorldObject(
            NextServerTime(),
            parsed.Kind,
            parsed.ObjectId,
            parsed.TypeName,
            parsed.FusTag,
            ownerActorNr: parsed.OwnerActorNr,
            trailingPayload: parsed.Trailing);
        var n = BroadcastInitReadyExcept(room, peer, echo, tag: "match_tx_CreateWorldObject");
        Console.WriteLine(
            $"[match-host] TX relay CreateWorldObject id={parsed.ObjectId} " +
            $"entity='{parsed.TypeName}' (tracked round) → peers={n} len={echo.Length}");
    }

    /// <summary>
    /// Phone-host clears leftover drops / planted entities between rounds. Dedicated host
    /// DestroyWorldObject's every tracked non-scene Entity id (not LivingPawns, not scene
    /// managers). BombManager scene id=8 is never destroyed — BombPlanted=false +
    /// BombPlantedUtc cleared + PendingBombPlant=false + fresh bomberId on PreStart;
    /// plant Rpc is evidence-only during RoundLive (no deferred invent). PlantedBombController is local
    /// (cleared by relayed field=6 nzu / client phase respawn) — no evidenced BombManager
    /// reset Rpc to invent.
    /// </summary>
    private void DestroyTrackedRoundEntities(MatchRoom room, string reason)
    {
        List<(short Id, string Name)> toDestroy;
        lock (_roomGate)
        {
            toDestroy = room.TrackedRoundEntities
                .Select(kv => (kv.Key, kv.Value))
                .ToList();
            room.TrackedRoundEntities.Clear();
        }

        if (toDestroy.Count == 0)
        {
            Console.WriteLine(
                $"[match-flow] bomb/round cleanup — no tracked entities reason={reason}");
            return;
        }

        var destroyed = 0;
        foreach (var (id, name) in toDestroy)
        {
            // Never Destroy bootstrap scene managers if somehow tracked.
            if (id >= 1 && id <= 8)
                continue;
            var destroyPkt = MatchCodec.BuildDestroyWorldObject(NextServerTime(), id);
            var n = BroadcastInitReady(room, destroyPkt, tag: "match_tx_DestroyWorldObject");
            destroyed++;
            Console.WriteLine(
                $"[match-flow] round cleanup destroy id={id} name='{name}' " +
                $"→ peers={n} reason={reason}");
        }
        Console.WriteLine(
            $"[match-flow] bomb/round cleanup done destroyed={destroyed}/{toDestroy.Count} " +
            $"reason={reason}");
    }

    /// <summary>
    /// ChatManager Rpc payload: single length byte + UTF-8 text (live Dedik captures
    /// <c>payloadLen=9</c> «херь», <c>payloadLen=10</c> «ну да»). Returns false if layout
    /// does not match — never invents a chat string from opaque bytes.
    /// </summary>
    private static bool TryDecodeChatManagerText(ReadOnlySpan<byte> payload, out string text)
    {
        text = "";
        if (payload.Length < 2)
            return false;
        var n = payload[0];
        if (n == 0 || n > payload.Length - 1)
            return false;
        if (n != payload.Length - 1)
            return false;
        try
        {
            text = System.Text.Encoding.UTF8.GetString(payload.Slice(1, n));
            return text.Length > 0;
        }
        catch
        {
            return false;
        }
    }

}

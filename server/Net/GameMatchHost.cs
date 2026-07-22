using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net;
using LiteNetLib;
using StandChillow.LanServer.Lan;
using StandChillow.LanServer.Net.Lobby;
using StandChillow.LanServer.Net.Match;

namespace StandChillow.LanServer.Net;

/// <summary>
/// Dedicated LAN match host: LiteNetLib UDP 7777, <c>AcceptIfKey(MyVerySecretKey)</c>,
/// ChannelsCount=3. Answers HandshakeRequest / JoinRoomRequest like phone LAN host.
/// Room dict key = fuy <b>password</b> (<c>fyi.boeh</c>) — live LAN uses literal <c>Dedik</c>
/// (chillow joke / metadata string), not empty <c>bfsq</c> "". Never replays capture blobs.
/// </summary>
public sealed class GameMatchHost : IDisposable
{
    public const int DefaultMatchPort = GameMatchClient.DefaultMatchPort;
    public const string ConnectionKey = GameMatchClient.ConnectionKey;
    public const byte ChannelsCount = GameMatchClient.ChannelsCount;

    /// <summary>
    /// LAN match CreateRoom password / dict key.
    /// Live phone JoinRoomRequest: <c>password='Dedik'</c> (capture
    /// <c>*_match_rx_JoinRoomRequest_len19.bin</c>). Chillow intentional joke literal
    /// in global-metadata — not LobbyId-derived.
    /// </summary>
    public const string LanCreateRoomPasswordKey = "Dedik";

    private sealed class MatchRoom
    {
        public required string PasswordKey { get; init; }
        public required string RoomId { get; init; }
        /// <summary>Next joiner nr; starts at 2 because actor 1 is always synthetic Server.</summary>
        public int NextActorNr { get; set; } = MatchHostActor.ActorNr + 1;
        public readonly HashSet<string> UserIds = new(StringComparer.Ordinal);
        public readonly List<(byte Nr, string Name)> Actors = new();
        public readonly Dictionary<(byte Actor, string Key), LobbyVariant> ActorProps = new();
        public readonly HashSet<byte> SpawnedPawns = new();
        /// <summary>Living fighting pawns keyed by net object id (joiner often allocates ≠129).</summary>
        public readonly Dictionary<short, LivingPawn> LivingPawns = new();
        public byte RoomC2 { get; set; }
        public MatchFlowState Flow { get; } = new();
    }

    private sealed class LivingPawn
    {
        public short ObjectId { get; init; }
        public byte OwnerActorNr { get; init; }
        public string TypeName { get; init; } = "";
        public byte FusTag { get; set; } = MatchSceneManagers.PlayerPawnFusTag;
        public byte[] TrailingPayload { get; set; } = [];
        public float PosX { get; set; }
        public float PosY { get; set; }
        public float PosZ { get; set; }
        public uint Seq { get; set; }
        public float TickTime { get; set; } = MatchSceneManagers.PawnState.BaseTime;
        public int StateCaptures { get; set; }
        /// <summary>Owner already TX fat WorldObjectState — prefer relay, skip host thin ticks.</summary>
        public bool OwnerSendsState { get; set; }
    }

    private sealed class MatchPeerState
    {
        public bool Handshaken { get; set; }
        public string? UserId { get; set; }
        public string? AppId { get; set; }
        public MatchRoom? Room { get; set; }
        public byte ActorNr { get; set; }
        public string? RosterName { get; set; }

        /// <summary>
        /// After Found: wait for joiner early identity before host managers+C2
        /// (phone host ~9s Unity load; dedicated must not unlock InitWaiting early).
        /// Gate: uid + from_lobby + (avatar | ping) — see MATCH_WORLD.md.
        /// Per-peer: each joiner needs its own INIT unlock on its LiteNetLib connection.
        /// </summary>
        public bool BootstrapPending { get; set; }
        /// <summary>True after managers×8 + C2=10 were TX'd to <b>this</b> peer.</summary>
        public bool BootstrapSent { get; set; }
        public bool JoinerUidSeen { get; set; }
        public bool JoinerFromLobbySeen { get; set; }
        public bool JoinerAvatarSeen { get; set; }
        public bool JoinerPingSeen { get; set; }
        public CancellationTokenSource? BootstrapTimeoutCts { get; set; }

        public bool IsJoinerIdentityReady =>
            JoinerUidSeen && JoinerFromLobbySeen && (JoinerAvatarSeen || JoinerPingSeen);
    }

    /// <summary>
    /// Soft fallback if joiner never sends early identity (Unity hung / probe).
    /// Phone host waited ~9s; 15s is loud fallback only.
    /// </summary>
    private static readonly TimeSpan JoinerReadySoftTimeout = TimeSpan.FromSeconds(15);

    private readonly NetManager _manager;
    private readonly EventBasedNetListener _listener;
    private readonly CancellationTokenSource _cts = new();
    private readonly List<Task> _tasks = new();
    private readonly ConcurrentDictionary<NetPeer, MatchPeerState> _peers = new();
    private readonly Dictionary<string, MatchRoom> _roomsByPassword = new(StringComparer.Ordinal);
    private readonly object _roomGate = new();
    private readonly string _captureDir;
    private readonly string _lobbyId;
    private int _captureIndex;
    private int _serverTimeSeed;

    // WorldObjectState flood control (same idea as GameMatchClient.AllowNoisyLog).
    private const int StateLogFirst = 8;
    private const int StateLogEveryK = 50;
    private static readonly TimeSpan StateSummaryInterval = TimeSpan.FromSeconds(3);
    private int _stateRxSeen;
    private int _stateRxSuppressed;
    private int _stateRelayLogged;
    private DateTime _lastStateSummaryUtc = DateTime.MinValue;

    // WeaponDropManager (scene id=4) WorldObjectRpc floods like State — rate-limit console.
    private const short WeaponDropManagerObjectId = 4;
    private const int DropRpcLogFirst = 8;
    private const int DropRpcLogEveryK = 50;
    private static readonly TimeSpan DropRpcSummaryInterval = TimeSpan.FromSeconds(3);
    private int _dropRpcRxSeen;
    private int _dropRpcRxSuppressed;
    private DateTime _lastDropRpcSummaryUtc = DateTime.MinValue;

    public GameMatchHost(string lobbyId, int port = DefaultMatchPort, IPAddress? advertiseHint = null)
    {
        _lobbyId = lobbyId;
        _captureDir = Path.Combine(AppContext.BaseDirectory, "captures");
        Directory.CreateDirectory(_captureDir);
        _serverTimeSeed = Environment.TickCount;

        _listener = new EventBasedNetListener();
        _manager = new NetManager(_listener)
        {
            AutoRecycle = true,
            IPv6Enabled = false,
            ChannelsCount = ChannelsCount,
        };

        _listener.ConnectionRequestEvent += request =>
        {
            var peek = request.Data.AvailableBytes;
            // fwv.OnConnectionRequest → AcceptIfKey(cwpw); empty key → ConnectionRejected.
            var accepted = request.AcceptIfKey(ConnectionKey);
            Console.WriteLine(
                accepted is not null
                    ? $"[match-host] CONNECT accept {request.RemoteEndPoint} dataLen={peek} keyOk"
                    : $"[match-host] CONNECT reject {request.RemoteEndPoint} dataLen={peek} (need '{ConnectionKey}')");
        };
        _listener.PeerConnectedEvent += peer =>
        {
            _peers[peer] = new MatchPeerState();
            Console.WriteLine($"[match-host] peer CONNECTED {peer.Address}:{peer.Port}");
        };
        _listener.PeerDisconnectedEvent += (peer, info) =>
        {
            _peers.TryRemove(peer, out var st);
            try { st?.BootstrapTimeoutCts?.Cancel(); }
            catch { /* ignore */ }
            if (st is { Room: { } room, ActorNr: > 0 })
                RemoveActorFromMatch(room, st.ActorNr, st.UserId, reason: info.Reason.ToString());
            Console.WriteLine($"[match-host] peer DISCONNECTED {peer.Address}:{peer.Port}: {info.Reason}");
        };
        _listener.NetworkReceiveEvent += OnNetworkReceive;
        // Phone host path (grp.OnNetworkLatencyUpdate) stores LiteNetLib latency and drives
        // actor prop ping; dedicated fwv stub is empty. Write peer.RoundTripTime as ping.
        _listener.NetworkLatencyUpdateEvent += OnPeerLatencyUpdate;
        _listener.NetworkErrorEvent += (ep, error) =>
            Console.WriteLine($"[match-host] error {ep}: {error}");

        if (!_manager.Start(port))
            throw new InvalidOperationException(
                $"Failed to bind match LiteNetLib on UDP {port} — " +
                "7777 must be a process singleton (no second GameMatchHost / no re-Start)");

        if (!_manager.IsRunning)
            throw new InvalidOperationException($"Match LiteNetLib reported not running after Start({port})");

        var lan = advertiseHint ?? LanInterfacePicker.PickLanBindAddress();
        Console.WriteLine(
            $"[match-host] LiteNetLib listen *:{port} key='{ConnectionKey}' channels={ChannelsCount} " +
            $"advertiseLAN={(lan?.ToString() ?? "?")} (SINGLETON — play/start must not rebind)");
        // Pre-create LAN room under password key Dedik so JoinOnly finds it as soon as op9 fires.
        EnsureLanRoom();

        _tasks.Add(Task.Run(PollLoop));
    }

    public int Port => _manager.LocalPort;
    /// <summary>True after the one-time UDP bind in the constructor — never Start() again.</summary>
    public bool IsListening => _manager.IsRunning;
    public int PeerCount => _manager.ConnectedPeersCount;
    public int RoomCount
    {
        get { lock (_roomGate) return _roomsByPassword.Count; }
    }

    /// <summary>
    /// Register/ensure the LAN match room under password key <see cref="LanCreateRoomPasswordKey"/>
    /// (<c>Dedik</c>). Host lookup is <c>fyi.boeh(password)</c> — room string is not the dict key.
    /// </summary>
    public void EnsureLanRoom()
    {
        lock (_roomGate)
        {
            if (_roomsByPassword.ContainsKey(LanCreateRoomPasswordKey))
                return;
            _roomsByPassword[LanCreateRoomPasswordKey] = CreateRoomWithServerHost(
                LanCreateRoomPasswordKey, _lobbyId);
            Console.WriteLine(
                $"[match-host] CreateRoom passwordKey='{LanCreateRoomPasswordKey}' " +
                $"(chillow LAN joke literal) bookkeepingRoomId='{_lobbyId}' " +
                $"hostActor={MatchHostActor.ActorNr}/'{MatchHostActor.Name}' " +
                $"team={MatchHostActor.Team}(Spectator) " +
                $"— JoinOnly must use fuy.password='{LanCreateRoomPasswordKey}'");
        }
    }

    private static MatchRoom CreateRoomWithServerHost(string passwordKey, string roomId)
    {
        var room = new MatchRoom
        {
            PasswordKey = passwordKey,
            RoomId = roomId,
            NextActorNr = MatchHostActor.ActorNr + 1,
        };
        // Actor 1 = lobby fake "Server" — master client / dedicated host, Spectators only.
        room.Actors.Add((MatchHostActor.ActorNr, MatchHostActor.Name));
        room.ActorProps[(MatchHostActor.ActorNr, MatchRoomPropKeys.Team)] =
            LobbyVariant.FromByte((byte)MatchHostActor.Team);
        room.ActorProps[(MatchHostActor.ActorNr, MatchRoomPropKeys.Uid)] =
            LobbyVariant.FromString(MatchHostActor.BootstrapUid);
        return room;
    }

    private int _stateTickCounter;

    private async Task PollLoop()
    {
        while (!_cts.IsCancellationRequested)
        {
            _manager.PollEvents();
            _stateTickCounter++;
            // Phone WorldObjectState ≈50ms; PollLoop is 15ms → every 3rd poll.
            if (_stateTickCounter % 3 == 0)
                TickLivingPawnStates();
            // Match flow (warmup→countdown→round) ~ every poll; cheap checks.
            TickMatchFlow();
            try { await Task.Delay(15, _cts.Token); }
            catch (OperationCanceledException) { break; }
        }
    }

    private void OnNetworkReceive(NetPeer peer, NetPacketReader reader, byte channel, DeliveryMethod method)
    {
        var payload = reader.GetRemainingBytes();
        try
        {
            HandleMatchPayload(peer, payload, channel);
        }
        catch (Exception ex)
        {
            Console.WriteLine(
                $"[match-host] parse/handle error from {peer} len={payload.Length}: {FormatException(ex)}");
            DumpCapture("match_err", payload);
        }
    }

    /// <summary>Flatten TypeInitializationException / AggregateException InnerException chain.</summary>
    private static string FormatException(Exception ex)
    {
        var parts = new List<string> { $"{ex.GetType().Name}: {ex.Message}" };
        for (var inner = ex.InnerException; inner is not null; inner = inner.InnerException)
            parts.Add($"{inner.GetType().Name}: {inner.Message}");
        return string.Join(" → ", parts);
    }

    private void HandleMatchPayload(NetPeer peer, byte[] payload, byte channel)
    {
        if (!MatchCodec.TryParseEnvelope(payload, out var flags, out var opcode, out var serverTime, out var bodyOffset))
        {
            Console.WriteLine($"[match-host] bad envelope from {peer} len={payload.Length} ch={channel}");
            DumpCapture("match_bad", payload);
            return;
        }

        var body = new LobbyReader(payload.AsSpan(bodyOffset));
        // WorldObjectState floods at ~20Hz/peer — rate-limit RX lines (keep CWO/team/Rpc loud).
        // WeaponDropManager Rpc (id=4) is similarly chatty mid-match.
        var dropRpcNoise = opcode == MatchOpcode.WorldObjectRpc
            && payload.Length >= bodyOffset + 2
            && BinaryPrimitives.ReadInt16LittleEndian(payload.AsSpan(bodyOffset, 2))
                == WeaponDropManagerObjectId;
        var logRx = opcode == MatchOpcode.WorldObjectState
            ? AllowStateLog()
            : dropRpcNoise
                ? AllowDropRpcLog()
                : true;
        if (logRx)
        {
            Console.WriteLine(
                $"[match-host] RX {MatchCodec.OpcodeName(opcode)} ({(byte)opcode}) from {peer.Address}:{peer.Port} " +
                $"flags={flags} ch={channel} len={payload.Length}");
        }

        switch (opcode)
        {
            case MatchOpcode.HandshakeRequest:
                HandleHandshake(peer, body, payload);
                break;
            case MatchOpcode.JoinRoomRequest:
                HandleJoinRoom(peer, body, payload);
                break;
            case MatchOpcode.SetProperty:
                HandleSetProperty(peer, flags, payload);
                break;
            case MatchOpcode.SetProperties:
                HandleSetProperties(peer, flags, payload);
                break;
            case MatchOpcode.CreateWorldObject:
                HandleCreateWorldObject(peer, flags, payload);
                break;
            case MatchOpcode.DestroyWorldObject:
                HandleDestroyWorldObject(peer, flags, payload);
                break;
            case MatchOpcode.WorldObjectRpc:
                HandleWorldObjectRpc(peer, flags, payload, logRpc: logRx);
                break;
            case MatchOpcode.WorldObjectState:
                // Owner→host Unreliable ticks (after CWO ack). Relay to INIT-ready peers
                // except sender — same flags=None as phone host TX.
                HandleWorldObjectStateRelay(peer, payload);
                break;
            case MatchOpcode.FetchServerTimeRequest:
                HandleFetchServerTime(peer, payload);
                break;
            default:
                Console.WriteLine($"[match-host] unhandled opcode {opcode} — dump");
                DumpCapture($"match_op{(byte)opcode}", payload);
                break;
        }
    }

    /// <summary>
    /// Client <c>fuo=2</c> empty body (<c>00 02</c>). Host replies <c>fyf.bodw</c>:
    /// HasServerTime + opcode 3 + TickCount — empty body.
    /// </summary>
    private void HandleFetchServerTime(NetPeer peer, byte[] raw)
    {
        DumpCapture("match_rx_FetchServerTimeRequest", raw);
        var stime = NextServerTime();
        var resp = MatchCodec.BuildFetchServerTimeResponse(stime);
        Send(peer, resp);
        Console.WriteLine(
            $"[match-host] TX FetchServerTimeResponse stime={stime} → {peer.Address}:{peer.Port}");
    }

    /// <summary>
    /// Rate-limit WorldObjectState console spam: first N + every Kth + periodic summary.
    /// </summary>
    private bool AllowStateLog()
    {
        var n = Interlocked.Increment(ref _stateRxSeen);
        if (n <= StateLogFirst || n % StateLogEveryK == 0)
            return true;

        Interlocked.Increment(ref _stateRxSuppressed);
        var now = DateTime.UtcNow;
        if (now - _lastStateSummaryUtc >= StateSummaryInterval)
        {
            _lastStateSummaryUtc = now;
            var suppressed = Interlocked.Exchange(ref _stateRxSuppressed, 0);
            Console.WriteLine(
                $"[match-host] WorldObjectState summary: rx={n} suppressed≈{suppressed} " +
                $"(logging first {StateLogFirst} + every {StateLogEveryK}th)");
        }
        return false;
    }

    /// <summary>Rate-limit WeaponDropManager WorldObjectRpc (objectId=4) console spam.</summary>
    private bool AllowDropRpcLog()
    {
        var n = Interlocked.Increment(ref _dropRpcRxSeen);
        if (n <= DropRpcLogFirst || n % DropRpcLogEveryK == 0)
            return true;

        Interlocked.Increment(ref _dropRpcRxSuppressed);
        var now = DateTime.UtcNow;
        if (now - _lastDropRpcSummaryUtc >= DropRpcSummaryInterval)
        {
            _lastDropRpcSummaryUtc = now;
            var suppressed = Interlocked.Exchange(ref _dropRpcRxSuppressed, 0);
            Console.WriteLine(
                $"[match-host] WorldObjectRpc id=4 summary: rx={n} suppressed≈{suppressed} " +
                $"(logging first {DropRpcLogFirst} + every {DropRpcLogEveryK}th)");
        }
        return false;
    }

    private void HandleHandshake(NetPeer peer, LobbyReader body, byte[] raw)
    {
        DumpCapture("match_rx_HandshakeRequest", raw);
        var req = MatchCodec.ParseHandshakeRequestBody(body);
        Console.WriteLine(
            $"[match-host] HandshakeRequest appId='{req.AppId}' userId='{req.UserId}' proto={req.ProtocolVersion}");

        if (!_peers.TryGetValue(peer, out var st))
            st = _peers[peer] = new MatchPeerState();

        HandshakeResult result;
        // Live phone sends com.Chillow.StandChillow; rejecting it → InvalidAppId →
        // HandshakeFailedException ("Connection to matchmaking server failed").
        if (!MatchAuth.IsAcceptedAppId(req.AppId))
            result = HandshakeResult.InvalidAppId;
        else if (string.IsNullOrWhiteSpace(req.UserId))
            result = HandshakeResult.InvalidUserId;
        else if (req.ProtocolVersion != MatchAuth.ProtocolVersion)
            result = HandshakeResult.InvalidProtocolVersion;
        else if (st.Handshaken)
            result = HandshakeResult.AlreadyAuthenticated;
        else
            result = HandshakeResult.Success;

        if (result == HandshakeResult.Success)
        {
            st.Handshaken = true;
            st.UserId = req.UserId;
            st.AppId = req.AppId;
        }

        // Live Success (phone host RX): 04 01 <i32 serverTime> 00 01
        // = HasServerTime + HandshakeResponse + fzi=Success + bool=true (fyj.SendResponse).
        var okFlag = result == HandshakeResult.Success;
        var resp = MatchCodec.BuildHandshakeResponse(result, okFlag, NextServerTime());
        Send(peer, resp);
        DumpCapture("match_tx_HandshakeResponse", resp);
        Console.WriteLine(
            $"[match-host] TX HandshakeResponse result={result} ({(byte)result}) okFlag={okFlag} " +
            $"len={resp.Length} hex={Convert.ToHexString(resp)}");
    }

    private void HandleJoinRoom(NetPeer peer, LobbyReader body, byte[] raw)
    {
        DumpCapture("match_rx_JoinRoomRequest", raw);
        if (!_peers.TryGetValue(peer, out var st) || !st.Handshaken)
        {
            Console.WriteLine("[match-host] JoinRoom before handshake — ignore");
            return;
        }

        var req = MatchCodec.ParseJoinRoomRequestBody(body);
        // Dict lookup = password only (fyi.boeh). Live password='Dedik' (chillow joke).
        // room (cwgt) = participant roster string — who should load into катка.
        // Live 'влал' alone is consistent with per-client lobby illusion (Server+self).
        Console.WriteLine(
            $"[match-host] JoinRoomRequest room={MatchRoomField.Describe(req.Room)} " +
            $"mode={req.Mode} " +
            $"password={(req.Password.Length == 0 ? "(empty)" : $"'{req.Password}'")} " +
            $"hasCreateOptions={req.HasCreateOptions} " +
            $"(lookupKey=password; expect '{LanCreateRoomPasswordKey}')");

        // CreateOnly / JoinOrCreate may register under the password they send.
        // JoinOnly: ensure Dedik LAN room (Play may race slightly before EnsureLanRoom).
        if (req.Mode is JoinRoomMode.CreateOnly or JoinRoomMode.JoinOrCreate)
            EnsureRoom(req.Password, string.IsNullOrEmpty(req.Room) ? _lobbyId : req.Room);
        else
            EnsureLanRoom();

        MatchRoom? room;
        lock (_roomGate)
            _roomsByPassword.TryGetValue(req.Password, out room);

        JoinRoomResult result;
        byte? actor = null;

        if (room is null)
        {
            // JoinOnly miss → RoomNotFound (fyk SendJoinRoomResponse result=7).
            var known = string.Join(", ",
                _roomsByPassword.Keys.Select(k => k.Length == 0 ? "\"\"" : $"'{k}'"));
            Console.WriteLine(
                $"[match-host] JoinRoom dict miss password='{req.Password}' " +
                $"(registered keys: [{known}]) — RoomNotFound");
            result = JoinRoomResult.RoomNotFound;
        }
        else if (st.UserId is { } uid && room.UserIds.Contains(uid))
        {
            result = JoinRoomResult.ActorWithSameUserIdExists;
        }
        else
        {
            // Accept JoinOnly when password hits Dedik. Do not gate on room==LobbyId —
            // room is roster semantics, not the dict key. Multi-name encoding TBD;
            // when lobby illusion is dropped, expect fuller roster strings here.
            byte nr;
            lock (_roomGate)
            {
                nr = (byte)Math.Clamp(room.NextActorNr++, 1, 255);
                if (st.UserId is { } u)
                    room.UserIds.Add(u);
                var nick = string.IsNullOrWhiteSpace(req.Room) ? $"actor{nr}" : req.Room;
                room.Actors.Add((nr, nick));
                st.RosterName = nick;
            }
            st.Room = room;
            st.ActorNr = nr;
            actor = nr;
            result = JoinRoomResult.Found;
            Console.WriteLine(
                $"[match-host] JoinRoom Found passwordKey='{room.PasswordKey}' " +
                $"roster={MatchRoomField.Describe(req.Room)} actorNr={nr} " +
                $"(host={MatchHostActor.ActorNr}/'{MatchHostActor.Name}' Spectator) " +
                $"userId='{st.UserId}'");
        }

        MatchGapRoom? gap = null;
        if (result == JoinRoomResult.Found && room is not null && actor is { } assigned)
        {
            List<(byte Nr, string Name)> actorsSnap;
            byte roomC2;
            lock (_roomGate)
            {
                actorsSnap = room.Actors.ToList();
                roomC2 = room.RoomC2;
            }

            // Phone-host probe (20260722_003523_* / run-20260722_073504): thin Found ~148B —
            // room props + actor names only (empty gak props). Identity/avatar come post-Found.
            // Match channel = shared truth (all actors). Lobby stays Server+self illusion for N>4.
            // MaxActorsHint ≥ live roster so Found never advertises fewer slots than actors present.
            var maxHint = (byte)Math.Clamp(
                Math.Max(MatchGapDefaults.MaxActorsHint, actorsSnap.Count), 1, 255);
            gap = new MatchGapRoom
            {
                RoomName = LanCreateRoomPasswordKey,
                Open = true,
                MaxActorsHint = maxHint,
                RoomProps = MatchGapDefaults.BuildRoomProps(
                    LobbyPropKeys.DefaultGameModeId,
                    LobbyPropKeys.DefaultSelectedLevel,
                    c2: roomC2),
                Actors = actorsSnap.Select(a => new MatchGapActor
                {
                    ActorNr = a.Nr,
                    Name = a.Name,
                    Props = null,
                    Flag = false,
                }).ToList(),
                TrailingByte = MatchGapDefaults.TrailingByte,
            };
        }

        var resp = MatchCodec.BuildJoinRoomResponse(
            result,
            NextServerTime(),
            actor,
            debugMessage: result == JoinRoomResult.Found ? LanCreateRoomPasswordKey : null,
            gap: gap);
        Send(peer, resp);
        DumpCapture("match_tx_JoinRoomResponse", resp);
        Console.WriteLine(
            $"[match-host] TX JoinRoomResponse result={MatchCodec.JoinRoomResultName(result)} " +
            $"actor={(actor?.ToString() ?? "-")} gap={(gap is null ? "none" : $"props={gap.Value.RoomProps.Count} actors={gap.Value.Actors.Count} C2={room?.RoomC2}")} " +
            $"len={resp.Length}");

        if (result == JoinRoomResult.Found && room is not null && actor is { } joinerNr)
        {
            // Dynamic roster: peers who already Found only knew actors-at-entry.
            // Push ActorJoinedEvent (fup) so early humans learn about this joiner.
            NotifyPeersActorJoined(room, peer, joinerNr, st.RosterName ?? $"actor{joinerNr}");
            BeginAwaitJoinerThenBootstrap(peer, room, joinerNr);
        }
    }

    /// <summary>
    /// Tell every other match peer about a newly Found human actor (<c>fuo=50</c>).
    /// Joiner already has the full roster in their own Found gap — exclude them.
    /// </summary>
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
        foreach (var mgr in MatchSceneManagers.Bootstrap)
        {
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
            $"managers={MatchSceneManagers.Bootstrap.Length} C2={MatchHostActor.C2AfterManagers} " +
            $"host={MatchHostActor.ActorNr}/'{MatchHostActor.Name}' team=Spectator; " +
            $"INIT unlocked — await team SetProperty then joiner CreateWorldObject echo)");

        // Mid-match / late join: C2=10 only clears InitWaiting. Push live room flow props +
        // other actors' identity so joiner is not stuck on WaitingPlayers with empty world.
        SendMatchStateSnapshotToPeer(peer, room, excludeActorNr: joinerActorNr);
        SendLivingPawnSnapshotToPeer(peer, room, excludeOwnerActorNr: joinerActorNr);
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
        List<LivingPawn> pawns;
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

            lock (_roomGate)
                room.ActorProps[(actorNr, key)] = value;

            // Rebroadcast uncompressed with HasServerTime (phone-host avatar path).
            var outPkt = MatchCodec.BuildSetProperty(NextServerTime(), actorNr, key, value);
            BroadcastRoom(room, outPkt, tag: "match_tx_SetProperty");

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
                NoteActorDeath(room, actorNr, value, source: "SetProperty");
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
            lock (_roomGate)
            {
                foreach (var (k, v) in props)
                {
                    if (v.Kind == LobbyVariantKind.Null)
                        room.ActorProps.Remove((actorNr, k));
                    else
                        room.ActorProps[(actorNr, k)] = v;
                    if (actorNr == 0 && k == MatchRoomPropKeys.C2 && v.Kind == LobbyVariantKind.Byte)
                        room.RoomC2 = v.Byte;
                }
            }

            var outPkt = MatchCodec.BuildSetProperties(NextServerTime(), actorNr, props);
            BroadcastRoom(room, outPkt, tag: "match_tx_SetProperties");

            foreach (var (k, v) in props)
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
                    NoteActorDeath(room, actorNr, v, source: "SetProperties");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[match-host] SetProperties parse failed: {ex.Message}");
        }
    }

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
                room.LivingPawns[parsed.ObjectId] = new LivingPawn
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
                NoteActorDeath(room, owner, LobbyVariant.FromInt(1), source: "DestroyWorldObject");
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
        List<(MatchRoom Room, LivingPawn Pawn)> snapshot;
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

    /// <summary>Reliable to every match peer in the room (props / ActorJoined fan-out).</summary>
    private int BroadcastRoom(MatchRoom room, byte[] payload, string tag)
    {
        var n = 0;
        foreach (var (p, st) in _peers)
        {
            if (st.Room == room)
            {
                Send(p, payload);
                n++;
            }
        }
        DumpCapture(tag, payload);
        return n;
    }

    /// <summary>Reliable to room peers except one (ActorJoined excludes the joiner).</summary>
    private int BroadcastRoomExcept(MatchRoom room, NetPeer except, byte[] payload, string tag)
    {
        var n = 0;
        foreach (var (p, st) in _peers)
        {
            if (st.Room == room && !ReferenceEquals(p, except))
            {
                Send(p, payload);
                n++;
            }
        }
        DumpCapture(tag, payload);
        return n;
    }

    /// <summary>
    /// Reliable to peers who finished INIT (BootstrapSent). Pawn CWO / rpc go here so
    /// late joiners do not get a live CWO and then a second copy from living-pawn snapshot.
    /// </summary>
    private int BroadcastInitReady(MatchRoom room, byte[] payload, string tag) =>
        BroadcastInitReadyExcept(room, except: null, payload, tag, dumpCapture: true);

    /// <summary>
    /// INIT-ready relay excluding sender — client already applied locally (CWO/Rpc/Destroy).
    /// </summary>
    private int BroadcastInitReadyExcept(
        MatchRoom room,
        NetPeer? except,
        byte[] payload,
        string? tag,
        bool dumpCapture = true)
    {
        var n = 0;
        foreach (var (p, st) in _peers)
        {
            if (st.Room != room || !st.BootstrapSent)
                continue;
            if (except is not null && ReferenceEquals(p, except))
                continue;
            Send(p, payload);
            n++;
        }
        if (dumpCapture && tag is not null)
            DumpCapture(tag, payload);
        return n;
    }

    private int BroadcastInitReadyUnreliable(MatchRoom room, byte[] payload, string? tag)
    {
        var n = 0;
        foreach (var (p, st) in _peers)
        {
            if (st.Room == room && st.BootstrapSent)
            {
                SendUnreliable(p, payload);
                n++;
            }
        }
        if (tag is not null)
            DumpCapture(tag, payload);
        return n;
    }

    /// <summary>Unreliable relay to INIT-ready match peers except the sender.</summary>
    private int BroadcastInitReadyUnreliableExcept(MatchRoom room, NetPeer except, byte[] payload)
    {
        var n = 0;
        foreach (var (p, st) in _peers)
        {
            if (st.Room == room && st.BootstrapSent && !ReferenceEquals(p, except))
            {
                SendUnreliable(p, payload);
                n++;
            }
        }
        return n;
    }

    private void SendAndDump(NetPeer peer, byte[] payload)
    {
        Send(peer, payload);
        if (!MatchCodec.TryParseEnvelope(payload, out _, out var op, out _, out _))
        {
            DumpCapture("match_tx_world", payload);
            return;
        }
        DumpCapture($"match_tx_{MatchCodec.OpcodeName(op)}", payload);
    }

    private void EnsureRoom(string passwordKey, string roomId)
    {
        lock (_roomGate)
        {
            if (_roomsByPassword.ContainsKey(passwordKey))
                return;
            _roomsByPassword[passwordKey] = CreateRoomWithServerHost(passwordKey, roomId);
            Console.WriteLine(
                $"[match-host] CreateRoom passwordKey={(passwordKey.Length == 0 ? "\"\"" : $"'{passwordKey}'")} " +
                $"roomId='{roomId}' hostActor={MatchHostActor.ActorNr}/'{MatchHostActor.Name}'");
        }
    }

    private int NextServerTime()
    {
        // Phone host advances serverTime with real clock (capture deltas ~ms).
        // Increment-only seed breaks Time/RoundStartTime deadlines for the match UI.
        var now = Environment.TickCount;
        while (true)
        {
            var prev = Volatile.Read(ref _serverTimeSeed);
            var next = now <= prev ? prev + 1 : now;
            if (Interlocked.CompareExchange(ref _serverTimeSeed, next, prev) == prev)
                return next;
        }
    }

    /// <summary>Server-time seconds — same unit as room <c>Time</c>/<c>RoundStartTime</c>
    /// and WorldObjectRpc timeValue. Client <c>dws.bfwi</c> / <c>NetManager.bfqt</c> =
    /// <c>(TickCount + offset) / 1000</c>. FetchServerTime header stays raw TickCount ms
    /// (<c>fyf.bodw</c>); do not put seconds into the HasServerTime i32.</summary>
    private double ServerTimeSeconds() => NextServerTime() / 1000.0;

    private void TryBeginMatchFlowIfBothTeams(MatchRoom room)
    {
        lock (_roomGate)
        {
            if (room.Flow.Phase != MatchFlowPhase.WaitingPlayers)
                return;
            if (!BothFightingTeamsPresent(room))
                return;
        }
        Console.WriteLine(
            "[match-host] match-flow: both teams have ≥1 player — will start Warmup on next tick");
    }

    private static bool BothFightingTeamsPresent(MatchRoom room)
    {
        var hasTr = false;
        var hasCt = false;
        foreach (var ((actor, key), value) in room.ActorProps)
        {
            if (key != MatchRoomPropKeys.Team || actor == MatchHostActor.ActorNr)
                continue;
            if (value.Kind != LobbyVariantKind.Byte)
                continue;
            var team = (MatchTeam)value.Byte;
            if (team == MatchTeam.Tr) hasTr = true;
            if (team == MatchTeam.Ct) hasCt = true;
        }
        return hasTr && hasCt;
    }

    private void TickMatchFlow()
    {
        List<MatchRoom> rooms;
        lock (_roomGate)
            rooms = _roomsByPassword.Values.ToList();

        foreach (var room in rooms)
            TickMatchFlowRoom(room);
    }

    private void TickMatchFlowRoom(MatchRoom room)
    {
        MatchFlowPhase phase;
        DateTime ends;
        string? pendingReason;
        MatchTeam pendingWinner;
        lock (_roomGate)
        {
            phase = room.Flow.Phase;
            ends = room.Flow.PhaseEndsUtc;
            pendingReason = room.Flow.PendingEndReason;
            pendingWinner = room.Flow.PendingWinner;
            if (phase == MatchFlowPhase.WaitingPlayers)
            {
                if (!BothFightingTeamsPresent(room))
                    return;
            }
            else if (phase == MatchFlowPhase.MatchOver)
                return;
            else if (pendingReason is not null && MatchFlowRules.AllowsWipeCheck(phase))
            {
                // Wipe / early end queued from death/plant path — do not wait for phase timer.
            }
            else if (DateTime.UtcNow < ends)
                return;
        }

        if (pendingReason is not null && MatchFlowRules.AllowsWipeCheck(phase))
        {
            EnterRoundEndPause(room, pendingWinner, pendingReason);
            return;
        }

        switch (phase)
        {
            case MatchFlowPhase.WaitingPlayers:
                EnterWarmup(room);
                break;
            case MatchFlowPhase.Warmup:
                EnterWarmupWillFinish(room);
                break;
            case MatchFlowPhase.WarmupWillFinish:
                EnterPurchasePhase(room, nextRound: true);
                break;
            case MatchFlowPhase.PurchasePhase:
                if (TryExtendPrepForAwaitingSpawn(room))
                    break;
                EnterRoundLive(room);
                break;
            case MatchFlowPhase.RoundLive:
                EnterRoundEndPause(room, MatchTeam.Ct, "timeout");
                break;
            case MatchFlowPhase.BombPlanted:
                EnterRoundEndPause(room, MatchTeam.Tr, "bomb-explode");
                break;
            case MatchFlowPhase.RoundEndPause:
                ContinueAfterRoundEnd(room);
                break;
        }
    }

    private void EnterWarmup(MatchRoom room)
    {
        var ends = DateTime.UtcNow + MatchFlowTestParams.Warmup;
        var nowSec = ServerTimeSeconds();
        var deadline = nowSec + MatchFlowTestParams.Warmup.TotalSeconds;
        lock (_roomGate)
        {
            room.Flow.Phase = MatchFlowPhase.Warmup;
            room.Flow.PhaseEndsUtc = ends;
            room.Flow.RoundIndex = 0;
            room.Flow.BombPlanted = false;
            room.Flow.PendingEndReason = null;
            room.Flow.DeadActors.Clear();
        }
        // Time/RoundStartTime are doubles in NetManager.bfqt units (TickCount/1000 seconds).
        // FetchServerTime HasServerTime header stays raw TickCount ms — client dws.bfwi divides.
        BroadcastRoomProps(room,
        [
            (MatchRoomPropKeys.C2, LobbyVariant.FromByte(MatchC2States.WarmUp)),
            (MatchRoomPropKeys.Time, LobbyVariant.FromDouble(deadline)),
            (MatchRoomPropKeys.RoundStartTime, LobbyVariant.FromDouble(nowSec)),
        ]);
        Console.WriteLine(
            $"[match-host] match-flow: Warmup C2={MatchC2States.WarmUp} " +
            $"{MatchFlowTestParams.Warmup.TotalSeconds:0}s movable (разминка — not freeze banner)");
    }

    /// <summary>
    /// WarmupWillFinish C2=22 — short freeze «MATCH WILL START IN» + assign <c>bomberId</c>
    /// (phone <c>cnr</c> / <c>bcb.opg</c>). Master then calls <c>BombManager.nyn</c> locally;
    /// dedicated has no evidenced give Rpc — see MATCH_WORLD.md.
    /// </summary>
    private void EnterWarmupWillFinish(MatchRoom room)
    {
        int bomberId;
        lock (_roomGate)
        {
            room.Flow.Phase = MatchFlowPhase.WarmupWillFinish;
            room.Flow.PhaseEndsUtc = DateTime.UtcNow + MatchFlowTestParams.WarmupWillFinish;
            room.Flow.BombPlanted = false;
            room.Flow.PendingEndReason = null;
            bomberId = PickBomberActorNr(room);
            room.Flow.BomberActorNr = bomberId;
        }

        var nowSec = ServerTimeSeconds();
        var deadline = nowSec + MatchFlowTestParams.WarmupWillFinish.TotalSeconds;
        BroadcastRoomProps(room,
        [
            (MatchRoomPropKeys.C2, LobbyVariant.FromByte(MatchC2States.WarmupWillFinish)),
            (MatchRoomPropKeys.RoundStartTime, LobbyVariant.FromDouble(nowSec)),
            (MatchRoomPropKeys.Time, LobbyVariant.FromDouble(deadline)),
            (MatchRoomPropKeys.BomberId, LobbyVariant.FromInt(bomberId)),
        ]);
        Console.WriteLine(
            $"[match-host] match-flow: PreStart C2={MatchC2States.WarmupWillFinish} " +
            $"{MatchFlowTestParams.WarmupWillFinish.TotalSeconds:0}s countdown " +
            $"bomberId={bomberId} (cnr/bcb.opg; BombManager.nyn is master-local — no host Rpc invent)");
    }

    /// <summary>
    /// PurchasePhase C2=31 — visible 10s «подготовка к раунду» every round (including first).
    /// Re-picks <c>bomberId</c> on rounds after the first (warmup already assigned round 1).
    /// </summary>
    private void EnterPurchasePhase(MatchRoom room, bool nextRound)
    {
        int round;
        int bomberId;
        lock (_roomGate)
        {
            round = nextRound ? room.Flow.RoundIndex + 1 : room.Flow.RoundIndex;
            if (round < 1) round = 1;
            room.Flow.RoundIndex = round;
            room.Flow.Phase = MatchFlowPhase.PurchasePhase;
            room.Flow.PhaseEndsUtc = DateTime.UtcNow + MatchFlowTestParams.PurchasePhase;
            room.Flow.BombPlanted = false;
            room.Flow.PendingEndReason = null;
            room.Flow.PrepSpawnExtensionUsed = false;
            room.Flow.DeadActors.Clear();
            // Round 1: keep bomber chosen on C2=22. Later rounds: pick fresh living Tr.
            if (round <= 1 && room.Flow.BomberActorNr > 0)
                bomberId = room.Flow.BomberActorNr;
            else
            {
                bomberId = PickBomberActorNr(room);
                room.Flow.BomberActorNr = bomberId;
            }
        }

        var nowSec = ServerTimeSeconds();
        var deadline = nowSec + MatchFlowTestParams.PurchasePhase.TotalSeconds;
        BroadcastRoomProps(room,
        [
            (MatchRoomPropKeys.C2, LobbyVariant.FromByte(MatchC2States.PurchasePhase)),
            (MatchRoomPropKeys.Round, LobbyVariant.FromInt(round)),
            (MatchRoomPropKeys.RoundCount, LobbyVariant.FromInt(MatchFlowTestParams.TotalRounds)),
            (MatchRoomPropKeys.RoundStartTime, LobbyVariant.FromDouble(nowSec)),
            (MatchRoomPropKeys.Time, LobbyVariant.FromDouble(deadline)),
            (MatchRoomPropKeys.BomberId, LobbyVariant.FromInt(bomberId)),
        ]);
        ClearFighterDeathFlags(room);
        Console.WriteLine(
            $"[match-host] match-flow: Prep C2={MatchC2States.PurchasePhase} 10s " +
            $"round={round}/{MatchFlowTestParams.TotalRounds} bomberId={bomberId}");
    }

    private void EnterRoundLive(MatchRoom room)
    {
        // Prep/prestart deaths: do not start 90s live with a team already wiped.
        if (TryResolveWipeImmediate(room, bombPlanted: false))
            return;

        int round;
        int bomberId;
        var txBomberFix = false;
        lock (_roomGate)
        {
            round = room.Flow.RoundIndex;
            // Last chance: living T on roster but bomberId still 0 (late Tr join race).
            if (room.Flow.BomberActorNr <= 0)
            {
                var pick = PickBomberActorNr(room);
                if (pick > 0)
                {
                    room.Flow.BomberActorNr = pick;
                    txBomberFix = true;
                }
            }
            bomberId = room.Flow.BomberActorNr;
            room.Flow.Phase = MatchFlowPhase.RoundLive;
            room.Flow.PhaseEndsUtc = DateTime.UtcNow + MatchFlowTestParams.RoundDuration;
            room.Flow.BombPlanted = false;
            room.Flow.PendingEndReason = null;
            room.Flow.DeadActors.Clear();
        }

        if (txBomberFix && bomberId > 0)
        {
            BroadcastRoomProps(room,
            [
                (MatchRoomPropKeys.BomberId, LobbyVariant.FromInt(bomberId)),
            ]);
            Console.WriteLine(
                $"[match-host] match-flow: bomberId={bomberId} (fixed 0→T at RoundLive edge)");
        }

        var nowSec = ServerTimeSeconds();
        var deadline = nowSec + MatchFlowTestParams.RoundDuration.TotalSeconds;
        BroadcastRoomProps(room,
        [
            (MatchRoomPropKeys.C2, LobbyVariant.FromByte(MatchC2States.MatchStarted)),
            (MatchRoomPropKeys.Round, LobbyVariant.FromInt(round)),
            (MatchRoomPropKeys.RoundCount, LobbyVariant.FromInt(MatchFlowTestParams.TotalRounds)),
            (MatchRoomPropKeys.RoundStartTime, LobbyVariant.FromDouble(nowSec)),
            (MatchRoomPropKeys.Time, LobbyVariant.FromDouble(deadline)),
        ]);
        SetAllFightersMoney(room, MatchFlowTestParams.RoundStartMoney);
        ClearFighterDeathFlags(room);
        Console.WriteLine(
            $"[match-host] match-flow: RoundLive C2={MatchC2States.MatchStarted} " +
            $"round={round}/{MatchFlowTestParams.TotalRounds} " +
            $"duration={MatchFlowTestParams.RoundDuration.TotalSeconds:0}s " +
            $"money={MatchFlowTestParams.RoundStartMoney} bomberId={bomberId}");
    }

    private void TryEnterBombPlanted(MatchRoom room, byte sourceRpc)
    {
        lock (_roomGate)
        {
            if (room.Flow.Phase != MatchFlowPhase.RoundLive || room.Flow.BombPlanted)
                return;
            room.Flow.BombPlanted = true;
            room.Flow.Phase = MatchFlowPhase.BombPlanted;
            room.Flow.PhaseEndsUtc = DateTime.UtcNow + MatchFlowTestParams.BombFuse;
        }

        var nowSec = ServerTimeSeconds();
        var deadline = nowSec + MatchFlowTestParams.BombFuse.TotalSeconds;
        BroadcastRoomProps(room,
        [
            (MatchRoomPropKeys.C2, LobbyVariant.FromByte(MatchC2States.BombPlanted)),
            (MatchRoomPropKeys.Time, LobbyVariant.FromDouble(deadline)),
            (MatchRoomPropKeys.RoundStartTime, LobbyVariant.FromDouble(nowSec)),
        ]);
        Console.WriteLine(
            $"[match-host] match-flow: BombPlanted C2={MatchC2States.BombPlanted} " +
            $"fuse={MatchFlowTestParams.BombFuse.TotalSeconds:0}s (BombManager rpc={sourceRpc})");
    }

    /// <summary>
    /// BombManager Rpc(6) <c>nzu(bool, byte, float)</c> — payload starts with bool via <c>fzr.bojd</c>.
    /// true → defuse (<c>vxr</c>) → CT win; false → explode FX (<c>vxq</c>) → T win.
    /// Ends round immediately while in BombPlanted (no fuse hang).
    /// </summary>
    private void HandleBombManagerNzu(MatchRoom room, byte[] payload)
    {
        if (payload.Length < 1)
        {
            Console.WriteLine(
                "[match-host] match-flow: BombManager rpc=6 nzu — empty payload, ignore");
            return;
        }

        var defused = payload[0] != 0;
        lock (_roomGate)
        {
            if (room.Flow.Phase is not (MatchFlowPhase.BombPlanted or MatchFlowPhase.RoundLive))
                return;
            if (room.Flow.PendingEndReason is not null)
                return;
            if (!room.Flow.BombPlanted && room.Flow.Phase != MatchFlowPhase.BombPlanted)
                return;

            room.Flow.PendingWinner = defused ? MatchTeam.Ct : MatchTeam.Tr;
            room.Flow.PendingEndReason = defused ? "bomb-defuse" : "bomb-explode-rpc";
        }

        Console.WriteLine(
            $"[match-host] match-flow: BombManager rpc=6 nzu defused={defused} " +
            $"→ {(defused ? "CT" : "T")} win (immediate RoundEnd)");
        EnterRoundEndPause(
            room,
            defused ? MatchTeam.Ct : MatchTeam.Tr,
            defused ? "bomb-defuse" : "bomb-explode-rpc");
    }

    /// <summary>
    /// Immediate full remove on peer disconnect / timeout: drop roster slot, props, pawns,
    /// TX <c>ActorLeftEvent</c> (fuo=51), then wipe-check remaining fighters via phone-host
    /// round-end (C2=101 + WinTeam/TrScore…) — never leave ghosts that force rejoin to actorNr=4/5.
    /// </summary>
    private void RemoveActorFromMatch(MatchRoom room, byte actorNr, string? userId, string reason)
    {
        if (actorNr == 0 || actorNr == MatchHostActor.ActorNr)
            return;

        List<short> pawnIds;
        lock (_roomGate)
        {
            if (userId is { } uid)
                room.UserIds.Remove(uid);
            room.Actors.RemoveAll(a => a.Nr == actorNr);
            room.SpawnedPawns.Remove(actorNr);
            room.Flow.DeadActors.Remove(actorNr);

            var propKeys = room.ActorProps.Keys.Where(k => k.Actor == actorNr).ToList();
            foreach (var k in propKeys)
                room.ActorProps.Remove(k);

            pawnIds = room.LivingPawns
                .Where(kv => kv.Value.OwnerActorNr == actorNr)
                .Select(kv => kv.Key)
                .ToList();
            foreach (var id in pawnIds)
                room.LivingPawns.Remove(id);
        }

        foreach (var id in pawnIds)
        {
            var destroy = MatchCodec.BuildDestroyWorldObject(NextServerTime(), id);
            var nD = BroadcastInitReady(room, destroy, tag: "match_tx_DestroyWorldObject");
            Console.WriteLine(
                $"[match-host] TX DestroyWorldObject id={id} → peers={nD} " +
                $"(disconnect actor={actorNr})");
        }

        var left = MatchCodec.BuildActorLeftEvent(NextServerTime(), actorNr);
        var n = BroadcastRoom(room, left, tag: "match_tx_ActorLeftEvent");
        Console.WriteLine(
            $"[match-host] TX ActorLeftEvent actor={actorNr} → peers={n} " +
            $"(full remove reason={reason})");

        // Mid-round leave: remaining team may win — same wipe rules, proper RoundEnd path.
        bool bombPlanted;
        MatchFlowPhase phase;
        lock (_roomGate)
        {
            phase = room.Flow.Phase;
            bombPlanted = room.Flow.BombPlanted;
        }
        if (MatchFlowRules.AllowsWipeCheck(phase))
        {
            Console.WriteLine(
                $"[match-host] match-flow: disconnect actor={actorNr} mid-{phase} — wipe check");
            TryResolveWipeImmediate(room, bombPlanted);
        }
    }

    private void EnterRoundEndPause(MatchRoom room, MatchTeam winner, string reason)
    {
        int round, scoreTr, scoreCt, coLossesTr, coLossesCt;
        byte mvpNr, mvpCode;
        int mvpCount = 0;
        lock (_roomGate)
        {
            // Ignore duplicate end while already pausing / over.
            if (room.Flow.Phase is MatchFlowPhase.RoundEndPause or MatchFlowPhase.MatchOver)
                return;

            round = room.Flow.RoundIndex;
            if (winner == MatchTeam.Tr)
            {
                room.Flow.ScoreTr++;
                room.Flow.CoLossesCt++;
                room.Flow.CoLossesTr = 0;
            }
            else if (winner == MatchTeam.Ct)
            {
                room.Flow.ScoreCt++;
                room.Flow.CoLossesTr++;
                room.Flow.CoLossesCt = 0;
            }
            scoreTr = room.Flow.ScoreTr;
            scoreCt = room.Flow.ScoreCt;
            coLossesTr = room.Flow.CoLossesTr;
            coLossesCt = room.Flow.CoLossesCt;
            (mvpNr, mvpCode) = PickRoundMvp(room, winner, reason);
            if (mvpNr != 0)
            {
                mvpCount = ReadActorIntProp(room, mvpNr, MatchRoomPropKeys.Mvp);
                if (mvpCount < 0)
                    mvpCount = 0;
                mvpCount++;
                room.ActorProps[(mvpNr, MatchRoomPropKeys.Mvp)] = LobbyVariant.FromInt(mvpCount);
            }
            room.Flow.Phase = MatchFlowPhase.RoundEndPause;
            room.Flow.PhaseEndsUtc = DateTime.UtcNow + MatchFlowTestParams.RoundEndPause;
            room.Flow.BombPlanted = false;
            room.Flow.PendingEndReason = null;
        }

        // Phone-host gold (run-20260722_100157 len≈151): actor mvp SetProperty first, then
        // room SetProperties: Time, {Tr|Ct}Score, {loser}CoLosses, {winner}CoLosses, WinTeam, C2=101.
        // NOT nested Score={Tr,Ct}, NOT C2=111/201, NOT winTeam/resultActor key names.
        if (mvpNr != 0)
        {
            var mvpPkt = MatchCodec.BuildSetProperty(
                NextServerTime(), mvpNr, MatchRoomPropKeys.Mvp, LobbyVariant.FromInt(mvpCount));
            BroadcastRoom(room, mvpPkt, tag: "match_tx_SetProperty");
            Console.WriteLine(
                $"[match-host] match-flow TX SetProperty actor={mvpNr} mvp={mvpCount}");
        }

        var nowSec = ServerTimeSeconds();
        var winTeamProps = BuildWinTeamProps(winner, mvpNr, mvpCode);
        var roomProps = BuildRoundEndRoomProps(
            nowSec, winner, scoreTr, scoreCt, coLossesTr, coLossesCt, winTeamProps);
        BroadcastRoomProps(room, roomProps);
        Console.WriteLine(
            $"[match-host] match-flow: RoundEnd C2={MatchC2States.MatchStarted} round={round} " +
            $"winner={winner} reason={reason} " +
            $"TrScore={scoreTr} CtScore={scoreCt} " +
            $"TrCoLosses={coLossesTr} CtCoLosses={coLossesCt} " +
            $"mvpPlayer={mvpNr} mvpCode={mvpCode} " +
            $"(phone-host WinTeam+TrScore/CtScore; not FinalHud C2={MatchC2States.FinalHud}; " +
            $"silent pause {MatchFlowTestParams.RoundEndPause.TotalSeconds:0}s)");
    }

    /// <summary>
    /// Allies phone-host round-end room bag order (gold len≈151):
    /// Time, winner flat score, loser CoLosses, winner CoLosses=0 streak reset, WinTeam, C2=101.
    /// </summary>
    private static List<(string Key, LobbyVariant Value)> BuildRoundEndRoomProps(
        double nowSec,
        MatchTeam winner,
        int scoreTr,
        int scoreCt,
        int coLossesTr,
        int coLossesCt,
        List<(string Key, LobbyVariant Value)> winTeamProps)
    {
        var props = new List<(string Key, LobbyVariant Value)>
        {
            (MatchRoomPropKeys.Time, LobbyVariant.FromDouble(nowSec)),
        };
        if (winner == MatchTeam.Tr)
        {
            props.Add((MatchRoomPropKeys.TrScore, LobbyVariant.FromInt(scoreTr)));
            props.Add((MatchRoomPropKeys.CtCoLosses, LobbyVariant.FromInt(coLossesCt)));
            props.Add((MatchRoomPropKeys.TrCoLosses, LobbyVariant.FromInt(coLossesTr)));
        }
        else
        {
            props.Add((MatchRoomPropKeys.CtScore, LobbyVariant.FromInt(scoreCt)));
            props.Add((MatchRoomPropKeys.TrCoLosses, LobbyVariant.FromInt(coLossesTr)));
            props.Add((MatchRoomPropKeys.CtCoLosses, LobbyVariant.FromInt(coLossesCt)));
        }

        props.Add((MatchRoomPropKeys.WinTeam, LobbyVariant.FromProps(winTeamProps)));
        props.Add((MatchRoomPropKeys.C2, LobbyVariant.FromByte(MatchC2States.MatchStarted)));
        return props;
    }

    private void ContinueAfterRoundEnd(MatchRoom room)
    {
        int round;
        lock (_roomGate)
            round = room.Flow.RoundIndex;

        if (round >= MatchFlowTestParams.TotalRounds)
        {
            lock (_roomGate)
            {
                room.Flow.Phase = MatchFlowPhase.MatchOver;
                room.Flow.PhaseEndsUtc = DateTime.MaxValue;
            }
            BroadcastRoomProps(room,
            [
                (MatchRoomPropKeys.C2, LobbyVariant.FromByte(MatchC2States.MatchResults)),
            ]);
            Console.WriteLine(
                $"[match-host] match-flow: MatchResults C2={MatchC2States.MatchResults} " +
                $"after {MatchFlowTestParams.TotalRounds} rounds " +
                "(FinalWinTeam wire unknown — C2 only)");
            return;
        }

        EnterPurchasePhase(room, nextRound: true);
    }

    /// <summary>
    /// Nested <c>WinTeam</c> for round end — gold keys <c>team</c>/<c>mvpPlayer</c>/<c>mvpCode</c>/
    /// <c>resultRoundType</c>/<c>resultRoundActor</c> as Byte. <c>mvpCode</c> = <see cref="MatchMvpCodes"/>.
    /// <c>resultRoundActor</c> is 0 on all four Allies gold outcomes (not mirrored from mvpPlayer).
    /// </summary>
    private static List<(string Key, LobbyVariant Value)> BuildWinTeamProps(
        MatchTeam winner,
        byte mvpNr,
        byte mvpCode) =>
    [
        (MatchWinTeamKeys.Team, LobbyVariant.FromByte((byte)winner)),
        (MatchWinTeamKeys.MvpPlayer, LobbyVariant.FromByte(mvpNr)),
        (MatchWinTeamKeys.MvpCode, LobbyVariant.FromByte(mvpCode)),
        (MatchWinTeamKeys.ResultRoundType, LobbyVariant.FromByte(0)),
        (MatchWinTeamKeys.ResultRoundActor, LobbyVariant.FromByte(0)),
    ];

    /// <summary>
    /// Pick MVP actor + <c>cns</c> code from round-end reason (phone <c>cnp.WinTeam</c> paths).
    /// Defuse → DefusingBomb + living/any Ct; plant/explode → PlantingBomb + bomberId;
    /// wipe/timeout → MostEliminations + max <c>round_kills</c> on winning team.
    /// </summary>
    private static (byte MvpActorNr, byte MvpCode) PickRoundMvp(
        MatchRoom room,
        MatchTeam winner,
        string reason)
    {
        byte code;
        byte preferred = 0;
        if (reason is "bomb-defuse")
        {
            code = MatchMvpCodes.DefusingBomb;
            preferred = PickTeamActor(room, MatchTeam.Ct, preferAlive: true);
        }
        else if (reason is "bomb-explode" or "bomb-explode-rpc")
        {
            code = MatchMvpCodes.PlantingBomb;
            preferred = room.Flow.BomberActorNr > 0
                && GetActorTeam(room, (byte)room.Flow.BomberActorNr) == MatchTeam.Tr
                    ? (byte)room.Flow.BomberActorNr
                    : PickTeamActor(room, MatchTeam.Tr, preferAlive: false);
        }
        else
        {
            // wipe-ct / wipe-tr / timeout / disconnect wipe — cnp uses MostEliminations.
            code = MatchMvpCodes.MostEliminations;
            preferred = PickTopRoundKillsActor(room, winner);
        }

        if (preferred == 0)
            preferred = PickTeamActor(room, winner, preferAlive: false);
        return (preferred, preferred == 0 ? MatchMvpCodes.None : code);
    }

    private static byte PickTopRoundKillsActor(MatchRoom room, MatchTeam team)
    {
        byte best = 0;
        var bestKills = -1;
        foreach (var ((actor, key), value) in room.ActorProps)
        {
            if (key != MatchRoomPropKeys.Team || actor == MatchHostActor.ActorNr)
                continue;
            if (value.Kind != LobbyVariantKind.Byte || (MatchTeam)value.Byte != team)
                continue;
            var kills = ReadActorIntProp(room, actor, MatchRoomPropKeys.RoundKills);
            if (kills < 0)
                kills = ReadActorIntProp(room, actor, MatchRoomPropKeys.Kills);
            if (kills < 0)
                kills = 0;
            if (kills > bestKills || (kills == bestKills && (best == 0 || actor < best)))
            {
                bestKills = kills;
                best = actor;
            }
        }
        return best;
    }

    private static byte PickTeamActor(MatchRoom room, MatchTeam team, bool preferAlive)
    {
        byte fallback = 0;
        foreach (var ((actor, key), value) in room.ActorProps)
        {
            if (key != MatchRoomPropKeys.Team || actor == MatchHostActor.ActorNr)
                continue;
            if (value.Kind != LobbyVariantKind.Byte || (MatchTeam)value.Byte != team)
                continue;
            fallback = actor;
            if (preferAlive && !MatchFlowRules.IsActorDead(room.Flow, room.ActorProps, actor))
                return actor;
            if (!preferAlive)
                return actor;
        }
        return fallback;
    }

    private static int ReadActorIntProp(MatchRoom room, byte actorNr, string key)
    {
        if (!room.ActorProps.TryGetValue((actorNr, key), out var v))
            return -1;
        return v.Kind switch
        {
            LobbyVariantKind.Int => v.Int,
            LobbyVariantKind.Byte => v.Byte,
            _ => -1,
        };
    }

    /// <summary>
    /// Mid-prep rejoin: Tr picked team while <c>bomberId=0</c> (prior T disconnect) — assign now.
    /// </summary>
    private void TryAssignBomberOnTrJoin(MatchRoom room, byte actorNr)
    {
        int bomberId;
        lock (_roomGate)
        {
            if (room.Flow.Phase != MatchFlowPhase.PurchasePhase)
                return;
            if (room.Flow.BomberActorNr > 0)
                return;
            if (GetActorTeam(room, actorNr) != MatchTeam.Tr)
                return;
            room.Flow.BomberActorNr = actorNr;
            bomberId = actorNr;
        }

        BroadcastRoomProps(room,
        [
            (MatchRoomPropKeys.BomberId, LobbyVariant.FromInt(bomberId)),
        ]);
        Console.WriteLine(
            $"[match-host] match-flow: bomberId={bomberId} (Tr joined during Prep, was 0)");
    }

    /// <summary>
    /// Fighter on Tr/Ct picked team but has not TX CreateWorldObject yet — defer Live once.
    /// </summary>
    private bool TryExtendPrepForAwaitingSpawn(MatchRoom room)
    {
        if (!FightersAwaitingSpawn(room))
            return false; // includes dead fighters — corpses must not extend prep

        double deadline;
        lock (_roomGate)
        {
            if (room.Flow.Phase != MatchFlowPhase.PurchasePhase)
                return false;
            if (room.Flow.PrepSpawnExtensionUsed)
                return false;
            room.Flow.PrepSpawnExtensionUsed = true;
            room.Flow.PhaseEndsUtc = DateTime.UtcNow + TimeSpan.FromSeconds(5);
            // Re-pick bomber if still 0 but a living T is now on roster.
            if (room.Flow.BomberActorNr <= 0)
            {
                var pick = PickBomberActorNr(room);
                if (pick > 0)
                    room.Flow.BomberActorNr = pick;
            }
        }

        var nowSec = ServerTimeSeconds();
        deadline = nowSec + 5.0;
        var props = new List<(string Key, LobbyVariant Value)>
        {
            (MatchRoomPropKeys.RoundStartTime, LobbyVariant.FromDouble(nowSec)),
            (MatchRoomPropKeys.Time, LobbyVariant.FromDouble(deadline)),
        };
        lock (_roomGate)
        {
            if (room.Flow.BomberActorNr > 0)
                props.Add((MatchRoomPropKeys.BomberId, LobbyVariant.FromInt(room.Flow.BomberActorNr)));
        }
        BroadcastRoomProps(room, props);
        Console.WriteLine(
            "[match-host] match-flow: Prep extended 5s — living fighter awaiting spawn " +
            $"(client Time refreshed; bomberId={(room.Flow.BomberActorNr)})");
        return true;
    }

    private bool FightersAwaitingSpawn(MatchRoom room)
    {
        lock (_roomGate)
        {
            return MatchFlowRules.FightersAwaitingSpawn(
                room.Flow,
                room.ActorProps,
                room.LivingPawns.Values.Select(p => p.OwnerActorNr));
        }
    }

    private static int PickBomberActorNr(MatchRoom room)
    {
        var candidates = new List<int>();
        foreach (var ((actor, key), value) in room.ActorProps)
        {
            if (key != MatchRoomPropKeys.Team || actor == MatchHostActor.ActorNr)
                continue;
            if (value.Kind != LobbyVariantKind.Byte)
                continue;
            if ((MatchTeam)value.Byte != MatchTeam.Tr)
                continue;
            if (MatchFlowRules.IsActorDead(room.Flow, room.ActorProps, (byte)actor))
                continue;
            candidates.Add(actor);
        }
        if (candidates.Count == 0)
            return 0;
        return candidates[Random.Shared.Next(candidates.Count)];
    }

    private void ClearFighterDeathFlags(MatchRoom room)
    {
        List<byte> actors;
        lock (_roomGate)
        {
            actors = room.Actors
                .Select(a => a.Nr)
                .Where(nr => nr != MatchHostActor.ActorNr)
                .ToList();
            foreach (var nr in actors)
                room.ActorProps[(nr, MatchRoomPropKeys.Death)] = LobbyVariant.FromInt(0);
        }
        foreach (var nr in actors)
        {
            var pkt = MatchCodec.BuildSetProperty(
                NextServerTime(), nr, MatchRoomPropKeys.Death, LobbyVariant.FromInt(0));
            BroadcastRoom(room, pkt, tag: "match_tx_SetProperty");
        }
    }

    private void NoteActorDeath(MatchRoom room, byte actorNr, LobbyVariant value, string source)
    {
        if (actorNr == 0 || actorNr == MatchHostActor.ActorNr)
            return;

        var dead = value.Kind switch
        {
            LobbyVariantKind.Int => value.Int != 0,
            LobbyVariantKind.Byte => value.Byte != 0,
            LobbyVariantKind.Bool => value.Bool,
            _ => false,
        };
        if (!dead)
            return;

        MatchTeam team;
        MatchFlowPhase phase;
        bool bombPlanted;
        lock (_roomGate)
        {
            if (!room.Flow.DeadActors.Add(actorNr))
                return; // already counted
            phase = room.Flow.Phase;
            bombPlanted = room.Flow.BombPlanted;
            team = GetActorTeam(room, actorNr);
        }

        Console.WriteLine(
            $"[match-host] match-flow: death actor={actorNr} team={team} via {source}");

        if (!MatchFlowRules.AllowsWipeCheck(phase))
            return;

        TryResolveWipeImmediate(room, bombPlanted);
    }

    private static void LogWipeImmediate(MatchTeam winner, string reason)
    {
        var side = reason == "wipe-tr" ? "T" : "CT";
        var winSide = winner == MatchTeam.Tr ? "T" : "CT";
        Console.WriteLine(
            $"[match-host] match-flow: wipe {side} → {winSide} win (immediate RoundEnd)");
    }

    /// <summary>
    /// Wipe resolve + immediate RoundEnd TX — do not wait for PollLoop tick (weapon-drop
    /// floods were delaying wipe→banner under load).
    /// </summary>
    /// <returns>True when RoundEnd was entered.</returns>
    private bool TryResolveWipeImmediate(MatchRoom room, bool bombPlanted)
    {
        if (!TryResolveWipe(room, bombPlanted, out var winner, out var reason))
            return false;
        LogWipeImmediate(winner, reason);
        EnterRoundEndPause(room, winner, reason);
        return true;
    }

    private bool TryResolveWipe(
        MatchRoom room, bool bombPlanted, out MatchTeam winner, out string reason)
    {
        winner = MatchTeam.None;
        reason = "";
        lock (_roomGate)
        {
            if (!MatchFlowRules.TryResolveWipe(
                    room.Flow, room.ActorProps, bombPlanted, out winner, out reason))
                return false;

            if (reason == "wipe-tr" && (bombPlanted || room.Flow.BombPlanted))
            {
                room.Flow.PendingEndReason = null;
                room.Flow.PendingWinner = MatchTeam.None;
                winner = MatchTeam.None;
                reason = "";
                Console.WriteLine(
                    "[match-host] match-flow: wipe T with bomb planted — wait fuse/defuse");
                return false;
            }
        }

        return true;
    }

    private static MatchTeam GetActorTeam(MatchRoom room, byte actorNr) =>
        MatchFlowRules.GetActorTeam(room.ActorProps, actorNr);

    private void BroadcastRoomProps(
        MatchRoom room,
        IReadOnlyList<(string Key, LobbyVariant Value)> props)
    {
        lock (_roomGate)
        {
            foreach (var (k, v) in props)
            {
                if (k == MatchRoomPropKeys.C2 && v.Kind == LobbyVariantKind.Byte)
                    room.RoomC2 = v.Byte;
                room.ActorProps[(0, k)] = v;
            }
        }
        var pkt = MatchCodec.BuildSetProperties(NextServerTime(), actorNr: 0, props);
        BroadcastRoom(room, pkt, tag: "match_tx_SetProperties");
        Console.WriteLine(
            $"[match-host] match-flow TX SetProperties actor=0 " +
            $"keys=[{string.Join(",", props.Select(p => $"{p.Key}={p.Value}"))}]");
    }

    private void SetAllFightersMoney(MatchRoom room, int money)
    {
        List<byte> actors;
        lock (_roomGate)
        {
            actors = room.Actors
                .Select(a => a.Nr)
                .Where(nr => nr != MatchHostActor.ActorNr)
                .ToList();
            foreach (var nr in actors)
                room.ActorProps[(nr, MatchRoomPropKeys.Money)] = LobbyVariant.FromInt(money);
        }
        foreach (var nr in actors)
        {
            var pkt = MatchCodec.BuildSetProperty(
                NextServerTime(), nr, MatchRoomPropKeys.Money, LobbyVariant.FromInt(money));
            BroadcastRoom(room, pkt, tag: "match_tx_SetProperty");
        }
        Console.WriteLine(
            $"[match-host] match-flow: money={money} → actors=[{string.Join(",", actors)}]");
    }

    private void Send(NetPeer peer, byte[] payload)
    {
        // gcv.Reliable → LiteNetLib ReliableOrdered (same as client match TX).
        peer.Send(payload, DeliveryMethod.ReliableOrdered);
    }

    private static void SendUnreliable(NetPeer peer, byte[] payload)
    {
        // WorldObjectState phone path: Unreliable + flags=None.
        peer.Send(payload, DeliveryMethod.Unreliable);
    }

    private void DumpCapture(string tag, byte[] payload)
    {
        try
        {
            var i = Interlocked.Increment(ref _captureIndex);
            var path = Path.Combine(
                _captureDir,
                $"{DateTime.UtcNow:yyyyMMdd_HHmmss}_{i:D3}_{tag}_len{payload.Length}.bin");
            File.WriteAllBytes(path, payload);
            Console.WriteLine($"[match-host] captured {path}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[match-host] capture failed: {ex.Message}");
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        foreach (var st in _peers.Values)
        {
            try { st.BootstrapTimeoutCts?.Cancel(); }
            catch { /* ignore */ }
        }
        try { Task.WhenAll(_tasks).Wait(800); } catch { /* ignore */ }
        _manager.Stop();
        _cts.Dispose();
    }
}

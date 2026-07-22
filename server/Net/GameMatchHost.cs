using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net;
using LiteNetLib;
using StandChillow.LanServer.Lan;
using StandChillow.LanServer.Net.Lobby;
using StandChillow.LanServer.Net.Match;
using StandChillow.LanServer.Net.Match.Host;

namespace StandChillow.LanServer.Net;

/// <summary>
/// Dedicated LAN match host: LiteNetLib UDP 7777, <c>AcceptIfKey(MyVerySecretKey)</c>,
/// ChannelsCount=3. Answers HandshakeRequest / JoinRoomRequest like phone LAN host.
/// Room dict key = fuy <b>password</b> (<c>fyi.boeh</c>) — live LAN uses literal <c>Dedik</c>
/// (chillow joke / metadata string), not empty <c>bfsq</c> "". Never replays capture blobs.
/// Split across partials under <c>Match/Host/</c> and mode flow under <c>Match/Modes/Ranked2v2/</c>.
/// </summary>
public sealed partial class GameMatchHost : IDisposable
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


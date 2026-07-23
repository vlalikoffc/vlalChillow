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

    /// <summary>
    /// Match gap <c>C0</c>/<c>C1</c> — must follow lobby <c>GameModeId</c>/<c>SelectedLevels[0]</c>.
    /// JoinRoomResponse previously hardcoded Ranked2v2/Sandstone, so /mode duel still loaded союзники.
    /// </summary>
    private string _matchGameModeId = LobbyPropKeys.DefaultGameModeId;
    private string _matchSelectedLevel = LobbyPropKeys.DefaultSelectedLevel;

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

    /// <summary>
    /// userId → last fighting team before disconnect. Used to restore mid-match reconnects
    /// (new actorNr after ActorLeft). Cleared on HardReset / successful restore / spectator fallback.
    /// </summary>
    private readonly Dictionary<string, MatchTeam> _reconnectTeams = new(StringComparer.Ordinal);

    private static readonly TimeSpan ReconnectRestoreSettle = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan ReconnectRestoreTimeout = TimeSpan.FromSeconds(5);
    /// <summary>After MatchResults C2=205 — delay before tearing down 7777 and lobby idle.</summary>
    private static readonly TimeSpan MatchOverReturnToLobbyDelay = TimeSpan.FromSeconds(5);

    private readonly int _listenPort;
    private volatile bool _acceptMatchConnections = true;

    /// <summary>
    /// Mid-match ChatManager text that looks like a lobby slash command (<c>/set start</c>, …).
    /// Payload decode is length-prefixed UTF-8 from live ChatManager Rpc captures — not invented.
    /// Lobby OpChat cannot see these; wire this from <see cref="GameNetHost"/>.
    /// </summary>
    public Action<IPAddress, string>? OnMatchChatSlashCommand { get; set; }

    /// <summary>
    /// Fired once when MatchOver + LobbyReturnPending timer elapses (wins-needed / series over).
    /// Lobby host should HardReset, stop 7777 advertise, restore idle lobby (keep mode/map).
    /// </summary>
    public Action? OnMatchOverReturnToLobby { get; set; }

    /// <summary>
    /// Host-originated debug chat (Server nick) when <see cref="MatchHostSettings.DebugMatchChat"/>
    /// is on. Wired from <see cref="GameNetHost.BroadcastConsoleChat"/> — same lobby OpChat path
    /// phones already see (not silent slash commands).
    /// </summary>
    public Action<string>? OnServerDebugChat { get; set; }

    /// <summary>
    /// No-op unless <see cref="MatchHostSettings.DebugMatchChat"/>. Posts via
    /// <see cref="OnServerDebugChat"/> (lobby OpChat as Server).
    /// </summary>
    private void PostServerDebugChat(string text)
    {
        if (!MatchHostSettings.DebugMatchChat) return;
        text = text.Trim();
        if (text.Length == 0) return;
        try { OnServerDebugChat?.Invoke(text); }
        catch (Exception ex)
        {
            Console.WriteLine($"[match-host] debug-chat failed: {ex.Message}");
        }
    }

    private static string DebugTeamTag(MatchTeam team) => team switch
    {
        MatchTeam.Tr => "T",
        MatchTeam.Ct => "CT",
        _ => "?",
    };

    private static string FormatRoundEndReasonRu(string reason) => reason switch
    {
        "wipe-ct" => "вайп CT",
        "wipe-tr" => "вайп T",
        "bomb-defuse" => "дефьюз",
        "bomb-explode" or "bomb-explode-rpc" => "взрыв",
        "timeout" => "тайм",
        _ => reason,
    };

    public GameMatchHost(string lobbyId, int port = DefaultMatchPort, IPAddress? advertiseHint = null)
    {
        _lobbyId = lobbyId;
        _listenPort = port;
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
            if (!_acceptMatchConnections || !_manager.IsRunning)
            {
                request.Reject();
                Console.WriteLine(
                    $"[match-host] CONNECT reject {request.RemoteEndPoint} dataLen={peek} " +
                    "(match host suspended / lobby idle)");
                return;
            }
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
                "7777 must be a process singleton (no second GameMatchHost while first is bound)");

        if (!_manager.IsRunning)
            throw new InvalidOperationException($"Match LiteNetLib reported not running after Start({port})");

        var lan = advertiseHint ?? LanInterfacePicker.PickLanBindAddress();
        Console.WriteLine(
            $"[match-host] LiteNetLib listen *:{port} key='{ConnectionKey}' channels={ChannelsCount} " +
            $"advertiseLAN={(lan?.ToString() ?? "?")} (one bind; Stop after MatchOver, Start again on /play)");
        // Pre-create LAN room under password key Dedik so JoinOnly finds it as soon as op9 fires.
        EnsureLanRoom();

        _tasks.Add(Task.Run(PollLoop));
    }

    public int Port => _manager.IsRunning ? _manager.LocalPort : _listenPort;
    /// <summary>True while match LiteNetLib is bound (false after MatchOver Suspend).</summary>
    public bool IsListening => _manager.IsRunning;

    /// <summary>
    /// After series MatchResults + delay: disconnect peers / clear Dedik, then <c>Stop()</c> UDP
    /// so 7777 is down until the next <see cref="EnsureMatchListening"/>.
    /// </summary>
    public void SuspendMatchListen(string reason)
    {
        _acceptMatchConnections = false;
        HardResetMatchForNewStart(reason);
        if (_manager.IsRunning)
        {
            try { _manager.Stop(); }
            catch (Exception ex)
            {
                Console.WriteLine($"[match-host] Stop UDP {_listenPort} failed: {ex.Message}");
            }
        }
        Console.WriteLine(
            $"[match-host] match listen SUSPENDED UDP {_listenPort} ({reason}) — " +
            "lobby idle until /play");
    }

    /// <summary>
    /// Ensure UDP match port is bound before Play/op9. Re-<c>Start</c>s after
    /// <see cref="SuspendMatchListen"/>; no-op if already listening.
    /// </summary>
    public bool EnsureMatchListening()
    {
        if (_manager.IsRunning)
        {
            _acceptMatchConnections = true;
            return true;
        }

        if (!_manager.Start(_listenPort))
        {
            Console.WriteLine(
                $"[match-host] FATAL: failed to rebind UDP {_listenPort} after suspend");
            return false;
        }

        _acceptMatchConnections = true;
        EnsureLanRoom();
        Console.WriteLine(
            $"[match-host] LiteNetLib RESTARTED UDP {_listenPort} key='{ConnectionKey}'");
        return true;
    }

    /// <summary>
    /// MatchResults C2=205 + arm 5s lobby return (Allies first-to-N / Ranked-Escalation series over).
    /// </summary>
    private void EnterMatchResultsAndArmLobbyReturn(
        MatchRoom room, int scoreTr, int scoreCt, string logTag, int round, string? extra = null)
    {
        lock (_roomGate)
        {
            room.Flow.Phase = MatchFlowPhase.MatchOver;
            room.Flow.PhaseEndsUtc = DateTime.UtcNow + MatchOverReturnToLobbyDelay;
            room.Flow.LobbyReturnPending = true;
        }
        BroadcastRoomProps(room,
        [
            (MatchRoomPropKeys.C2, LobbyVariant.FromByte(MatchC2States.MatchResults)),
        ], reason: $"MatchResults Tr={scoreTr} Ct={scoreCt}");
        Console.WriteLine(
            $"[match-host] {logTag}: MatchResults C2={MatchC2States.MatchResults} " +
            $"Tr={scoreTr} Ct={scoreCt} after round={round}" +
            (extra is null ? "" : $" {extra}") +
            $" — lobby return in {MatchOverReturnToLobbyDelay.TotalSeconds:0}s");
    }

    /// <summary>
    /// Tick helper: when MatchOver + LobbyReturnPending and deadline elapsed, fire once.
    /// </summary>
    private bool TryFireMatchOverLobbyReturn(MatchRoom room, DateTime phaseEndsUtc)
    {
        bool fire;
        lock (_roomGate)
        {
            fire = room.Flow.Phase == MatchFlowPhase.MatchOver
                   && room.Flow.LobbyReturnPending
                   && DateTime.UtcNow >= phaseEndsUtc;
            if (fire)
                room.Flow.LobbyReturnPending = false;
        }
        if (!fire)
            return false;

        Console.WriteLine(
            "[match-host] match-over: 5s elapsed after MatchResults → return players to lobby");
        try { OnMatchOverReturnToLobby?.Invoke(); }
        catch (Exception ex)
        {
            Console.WriteLine($"[match-host] OnMatchOverReturnToLobby: {ex.Message}");
        }
        return true;
    }
    public int PeerCount => _manager.ConnectedPeersCount;
    public int RoomCount
    {
        get { lock (_roomGate) return _roomsByPassword.Count; }
    }

    /// <summary>
    /// Push lobby mode/map into match gap props (<c>C0</c>/<c>C1</c>) before JoinRoom Found.
    /// </summary>
    public void SetMatchSelection(string gameModeId, string selectedLevel)
    {
        if (string.IsNullOrWhiteSpace(gameModeId))
            gameModeId = LobbyPropKeys.DefaultGameModeId;
        if (string.IsNullOrWhiteSpace(selectedLevel))
            selectedLevel = LobbyPropKeys.DefaultSelectedLevel;

        lock (_roomGate)
        {
            _matchGameModeId = gameModeId.Trim();
            _matchSelectedLevel = selectedLevel.Trim();
        }

        Console.WriteLine(
            $"[match-host] match selection C0='{_matchGameModeId}' C1='{_matchSelectedLevel}'");
    }

    public (string GameModeId, string SelectedLevel) GetMatchSelection()
    {
        lock (_roomGate)
            return (_matchGameModeId, _matchSelectedLevel);
    }

    /// <summary>
    /// Host-authored <c>SetProperty team</c> for the match peer (<c>/set team</c>).
    /// Broadcasts like a normal team pick.
    /// </summary>
    public bool TryForceTeam(NetPeer peer, MatchTeam team, out string message)
    {
        if (!_peers.TryGetValue(peer, out var st) || st.Room is null || st.ActorNr == 0)
        {
            message = "не в матче — /set team только после JoinRoom";
            return false;
        }

        if (team is not (MatchTeam.Tr or MatchTeam.Ct or MatchTeam.Spectator))
        {
            message = "team: ct | tr | spectator";
            return false;
        }

        var room = st.Room;
        var actorNr = st.ActorNr;
        var value = LobbyVariant.FromByte((byte)team);
        lock (_roomGate)
            room.ActorProps[(actorNr, MatchRoomPropKeys.Team)] = value;

        var outPkt = MatchCodec.BuildSetProperty(NextServerTime(), actorNr, MatchRoomPropKeys.Team, value);
        BroadcastRoom(room, outPkt, tag: "match_tx_SetProperty_team_force");
        Console.WriteLine($"[match-host] /set team → actor={actorNr} team={team} (host-forced)");

        if (team is MatchTeam.Tr or MatchTeam.Ct)
        {
            TryBeginMatchFlowIfBothTeams(room);
            TryAssignBomberOnTrJoin(room, actorNr);
        }

        message = $"team → {team} (actor={actorNr})";
        return true;
    }

    /// <summary>Match peer by LAN IP (lobby and match use different UDP ports).</summary>
    public bool TryForceTeamByIp(IPAddress ip, MatchTeam team, out string message)
    {
        foreach (var (p, _) in _peers)
        {
            if (p.Address is null) continue;
            if (!p.Address.Equals(ip)) continue;
            return TryForceTeam(p, team, out message);
        }

        message = "матч-peer с этим IP не найден (зайди в игру / дождись JoinRoom)";
        return false;
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

    /// <summary>
    /// Host simulation tick — 128 Hz for wipe / fuse / phase-deadline detection.
    /// Wire timers (fuse 40s, pause 6s, …) stay gold durations; only the poll cadence
    /// is raised so decisive outcomes EnterRoundEnd on the same tick they become true.
    /// </summary>
    private static readonly TimeSpan HostTickPeriod = TimeSpan.FromMilliseconds(1000.0 / 128.0);

    private async Task PollLoop()
    {
        Console.WriteLine(
            $"[match-host] poll loop tickrate=128 Hz period={HostTickPeriod.TotalMilliseconds:0.###}ms " +
            "(wipe/fuse/phase checks every tick; WorldObjectState thin ticks ~every 3rd)");
        while (!_cts.IsCancellationRequested)
        {
            var tickStart = Environment.TickCount64;
            _manager.PollEvents();
            _stateTickCounter++;
            // Phone WorldObjectState ≈50ms; at 128 Hz ≈7.8ms → every 6th–7th poll ≈50ms.
            if (_stateTickCounter % 6 == 0)
                TickLivingPawnStates();
            // Match flow every tick: wipe / fuse expiry / phase deadlines.
            TickMatchFlow();
            var elapsed = Environment.TickCount64 - tickStart;
            var delayMs = (int)Math.Max(0, HostTickPeriod.TotalMilliseconds - elapsed);
            try { await Task.Delay(delayMs, _cts.Token); }
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

    /// <summary>
    /// True when Dedik room has progressed past a clean lobby-ready WaitingPlayers (scores,
    /// fighters, live phase, MatchOver, leftovers) — a second /play must tear down first.
    /// </summary>
    public bool HasStaleMatchState()
    {
        lock (_roomGate)
        {
            if (!_roomsByPassword.TryGetValue(LanCreateRoomPasswordKey, out var room))
                return false;
            if (room.Flow.Phase != MatchFlowPhase.WaitingPlayers)
                return true;
            if (room.Flow.RoundIndex != 0
                || room.Flow.ScoreTr != 0
                || room.Flow.ScoreCt != 0
                || room.Flow.BombPlanted
                || room.Flow.RoundEndCommittedRound != 0)
                return true;
            if (room.Actors.Count > 1)
                return true;
            if (room.LivingPawns.Count > 0 || room.TrackedRoundEntities.Count > 0)
                return true;
            if (room.RoomC2 > MatchC2States.WaitingPlayers)
                return true;
            return false;
        }
    }

    /// <summary>
    /// Hard reset before a rematch /play: stop flow on old room, Destroy leftover entities,
    /// drop match peers, replace Dedik room with a fresh WaitingPlayers host slot.
    /// Does <b>not</b> rebind UDP 7777 (singleton). Evidence: dedicated rematch hang was
    /// re-advertising op9 into MatchOver / mid-round state with stale actors/C2.
    /// </summary>
    public void HardResetMatchForNewStart(string reason)
    {
        Console.WriteLine(
            $"[match-flow] tearing down previous match before start ({reason})");

        MatchRoom? oldRoom;
        List<NetPeer> peersToDrop;
        List<short> pawnIds;
        lock (_roomGate)
        {
            _roomsByPassword.TryGetValue(LanCreateRoomPasswordKey, out oldRoom);
            peersToDrop = _peers
                .Where(kv => kv.Value.Room is not null || kv.Value.ActorNr > 0 || kv.Value.Handshaken)
                .Select(kv => kv.Key)
                .ToList();
            pawnIds = oldRoom?.LivingPawns.Keys.ToList() ?? new List<short>();
        }

        if (oldRoom is not null)
        {
            ClearBombAuthority(oldRoom, $"HardReset:{reason}");
            lock (_roomGate)
            {
                oldRoom.Flow.PendingEndReason = null;
                oldRoom.Flow.DeadActors.Clear();
                oldRoom.Flow.PendingCombatDestroy.Clear();
                oldRoom.Flow.Phase = MatchFlowPhase.MatchOver;
            }
            // Best-effort Destroy while peers may still be on the old scene.
            DestroyTrackedRoundEntities(oldRoom, reason: "teardown");
            foreach (var id in pawnIds)
            {
                var destroy = MatchCodec.BuildDestroyWorldObject(NextServerTime(), id);
                var n = BroadcastInitReady(oldRoom, destroy, tag: "match_tx_DestroyWorldObject");
                Console.WriteLine(
                    $"[match-flow] round cleanup destroy id={id} name='LivingPawn' " +
                    $"→ peers={n} reason=teardown");
            }
        }

        foreach (var peer in peersToDrop)
        {
            try
            {
                if (_peers.TryGetValue(peer, out var st))
                    ResetPeerStateForRematch(st);
                peer.Disconnect();
            }
            catch (Exception ex)
            {
                Console.WriteLine(
                    $"[match-flow] teardown disconnect peer failed: {ex.Message}");
            }
        }

        lock (_roomGate)
        {
            _roomsByPassword[LanCreateRoomPasswordKey] = CreateRoomWithServerHost(
                LanCreateRoomPasswordKey, _lobbyId);
            // Orphan any peer state still pointing at the old room instance.
            foreach (var st in _peers.Values)
                ResetPeerStateForRematch(st);
        }

        MatchHostSettings.MatchStartArmed = false;
        lock (_roomGate)
            _reconnectTeams.Clear();
        Console.WriteLine("[match-flow] previous match cleared");
    }

    private static void ResetPeerStateForRematch(MatchPeerState st)
    {
        try { st.BootstrapTimeoutCts?.Cancel(); }
        catch { /* ignore */ }
        st.BootstrapTimeoutCts = null;
        st.Room = null;
        st.ActorNr = 0;
        st.RosterName = null;
        st.Handshaken = false;
        st.UserId = null;
        st.AppId = null;
        st.BootstrapPending = false;
        st.BootstrapSent = false;
        st.JoinerUidSeen = false;
        st.JoinerFromLobbySeen = false;
        st.JoinerAvatarSeen = false;
        st.JoinerPingSeen = false;
        st.PendingReconnectTeam = null;
        st.ReconnectSpectatorFallbackDone = false;
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


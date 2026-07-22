using System.Net;
using System.Text;
using LiteNetLib;
using StandChillow.LanServer.Lan;
using StandChillow.LanServer.Net.Lobby;
using StandChillow.LanServer.Net.Match;

namespace StandChillow.LanServer.Net;

/// <summary>
/// ConnectAsClient match channel: after OpGameHostingStateChangedEvent the real client
/// (LanLobbyHelper.qxm → NetManager.bfqz(room) + bfqg(cui.balx(), endpoint) → LiteNetLibClient.bovt)
/// opens a second LiteNetLib connection to esz IP:port (live: 7777).
/// Post-connect: HandshakeRequest (<c>fuv</c>) then JoinRoomRequest (<c>fuy</c> / JoinOnly) on Success.
/// Captures are for analysis only — never replay into dedicated-host replies.
/// </summary>
public sealed class GameMatchClient : IDisposable
{
    /// <summary>Live phone game-host port from op9 (distinct from lobby 7778).</summary>
    public const int DefaultMatchPort = 7777;

    /// <summary>
    /// <c>fwv.cwpw</c> — match host <c>AcceptIfKey</c> / client <c>Connect(key)</c>.
    /// Live lobby CONNECT dataLen=17 = LiteNetLib <c>Put</c> ushort len + UTF-8 (2+15).
    /// Lobby host <c>Accept()</c> ignores it; match host does not.
    /// </summary>
    public const string ConnectionKey = "MyVerySecretKey";

    /// <summary><c>fwv</c> / <c>grp</c> LiteNetLib <c>ChannelsCount</c> (= 3).</summary>
    public const byte ChannelsCount = 3;

    /// <summary>
    /// Host may emit op9 before CreateRoom finishes; also try alternate room/password
    /// candidates proven from decompile (see <see cref="BuildJoinCandidates"/>).
    /// </summary>
    private const int MaxJoinAttempts = 8;
    private static readonly TimeSpan JoinRetryDelay = TimeSpan.FromMilliseconds(750);
    private static readonly TimeSpan IdleLogInterval = TimeSpan.FromSeconds(5);
    /// <summary>Full WorldObjectState / POST-FOUND flood logs for the first N, then summary.</summary>
    private const int NoisyLogFirst = 8;
    private const int NoisyLogEveryK = 50;
    private static readonly TimeSpan NoisySummaryInterval = TimeSpan.FromSeconds(3);
    /// <summary>Cap WorldObjectState .bin captures so long matches do not fill disk.</summary>
    private const int MaxWorldObjectStateCaptures = 12;
    /// <summary>
    /// If host bootstrap (C2=10) is slow/missing, still TX Spectator after this delay —
    /// phone OBT may force CT on the host actor; probe always self-assigns Spectator on wire.
    /// </summary>
    private static readonly TimeSpan SpectatorFallbackDelay = TimeSpan.FromSeconds(2.5);

    /// <summary>One JoinRoom attempt: room field + password field + why.</summary>
    private readonly record struct JoinCandidate(string Room, string Password, string Why);

    private readonly NetManager _manager;
    private readonly EventBasedNetListener _listener;
    private readonly CancellationTokenSource _cts = new();
    private readonly List<Task> _tasks = new();
    private readonly string _captureDir;
    private readonly bool _saveCaptures;
    private readonly Action? _onConnected;
    private readonly IPAddress? _bindAddress;
    private readonly string _appId;
    private readonly string _userId;
    /// <summary>fuy.cwgt primary — participant roster string (probe nickname on LAN).</summary>
    private string? _rosterRoom;
    /// <summary>LobbyId from lobby props — fallback / legacy bfqz candidate only.</summary>
    private string? _lobbyId;
    private string? _gameModeId;
    private JoinCandidate[] _joinCandidates = Array.Empty<JoinCandidate>();
    private IPEndPoint? _fallback;
    private bool _triedFallback;
    private int _captureIndex;
    private int _rxLogged;
    private int _txLogged;
    private int _postJoinRx;
    private int _wosSeen;
    private int _wosCaptures;
    private int _wosSuppressed;
    private DateTime _lastWosSummaryUtc = DateTime.MinValue;
    private NetPeer? _peer;
    private bool _handshakeSent;
    private int _joinAttempts;
    private bool _joinSucceeded;
    private bool _joinInFlight;
    private byte? _localActorNr;
    private bool _bootstrapReady;
    private bool _spectatorSent;
    private int _sceneManagersSeen;
    private DateTime _lastRxUtc = DateTime.UtcNow;
    private DateTime _lastIdleLogUtc = DateTime.UtcNow;

    public GameMatchClient(
        bool saveCaptures = true,
        Action? onConnected = null,
        IPAddress? bindAddress = null,
        string? appId = null,
        string? userId = null,
        string? roomId = null,
        string? gameModeId = null,
        string? rosterRoom = null)
    {
        _saveCaptures = saveCaptures;
        _onConnected = onConnected;
        _bindAddress = bindAddress ?? LanInterfacePicker.PickLanBindAddress();
        // Live phone→dedicated Handshake AppId = com.Chillow.StandChillow (not gamma).
        _appId = appId ?? MatchAuth.AppId;
        _userId = userId ?? Guid.NewGuid().ToString("N");
        _lobbyId = roomId;
        _gameModeId = gameModeId;
        // Live JoinRoom room field = roster nickname (illusion → self); not LobbyId.
        _rosterRoom = string.IsNullOrEmpty(rosterRoom) ? null : rosterRoom;
        _joinCandidates = BuildJoinCandidates(_rosterRoom, _lobbyId, _gameModeId);
        _captureDir = Path.Combine(AppContext.BaseDirectory, "captures");
        if (_saveCaptures)
            Directory.CreateDirectory(_captureDir);

        _listener = new EventBasedNetListener();
        _manager = new NetManager(_listener)
        {
            AutoRecycle = true,
            IPv6Enabled = false,
            ChannelsCount = ChannelsCount,
        };

        _listener.PeerConnectedEvent += peer =>
        {
            _peer = peer;
            _rxLogged = 0;
            _handshakeSent = false;
            _joinAttempts = 0;
            _joinSucceeded = false;
            _joinInFlight = false;
            _localActorNr = null;
            _bootstrapReady = false;
            _spectatorSent = false;
            _sceneManagersSeen = 0;
            _lastRxUtc = DateTime.UtcNow;
            Console.WriteLine(
                $"[match-net] CONNECTED to {peer.Address}:{peer.Port} (game channel, " +
                $"key='{ConnectionKey}', channels={ChannelsCount})");
            _onConnected?.Invoke();
            SendHandshake(peer);
        };
        _listener.PeerDisconnectedEvent += (peer, info) =>
        {
            Console.WriteLine($"[match-net] DISCONNECTED {peer.Address}:{peer.Port}: {info.Reason}");
            if (ReferenceEquals(_peer, peer))
                _peer = null;

            TryFallbackAfterFailure(info.Reason);
        };
        _listener.NetworkReceiveEvent += OnReceive;
        _listener.NetworkErrorEvent += (ep, error) =>
            Console.WriteLine($"[match-net] error {ep}: {error}");

        StartBound();
        _tasks.Add(Task.Run(PollLoop));
    }

    public bool IsConnected => _peer is { ConnectionState: ConnectionState.Connected };
    public IPEndPoint? Endpoint { get; private set; }
    public IPAddress? BindAddress => _bindAddress;
    public string AppId => _appId;
    public string UserId => _userId;
    /// <summary>Legacy alias — LobbyId fallback field (not the live JoinRoom room string).</summary>
    public string? RoomId => _lobbyId;
    public string? RosterRoom => _rosterRoom;
    public string? LobbyId => _lobbyId;
    public string? GameModeId => _gameModeId;
    public bool JoinSucceeded => _joinSucceeded;
    public int PostJoinRxCount => _postJoinRx;

    /// <summary>
    /// Update LobbyId / roster / mode and rebuild JoinRoom candidates.
    /// <paramref name="rosterRoom"/> is <c>fuy.cwgt</c> (participant nicknames);
    /// <paramref name="lobbyId"/> is only a fallback candidate.
    /// </summary>
    public void SetRoomId(string? lobbyId, string? gameModeId = null, string? rosterRoom = null)
    {
        if (lobbyId is not null)
            _lobbyId = lobbyId;
        if (gameModeId is not null)
            _gameModeId = gameModeId;
        if (rosterRoom is not null)
            _rosterRoom = rosterRoom;
        _joinCandidates = BuildJoinCandidates(_rosterRoom, _lobbyId, _gameModeId);
    }

    /// <summary>
    /// JoinRoom (room, password) candidates ordered by live evidence then decompile.
    /// Host dict key = fuy <b>password</b> (<c>fyi.boeh</c>) = chillow joke <c>Dedik</c>
    /// (UI may label it “room name” — wire field is still password).
    /// Room field = participant roster string (probe nickname when joining phone host).
    /// </summary>
    private static JoinCandidate[] BuildJoinCandidates(
        string? rosterRoom,
        string? lobbyId,
        string? gameModeId)
    {
        var list = new List<JoinCandidate>();
        var roster = rosterRoom ?? "";
        var id = lobbyId ?? "";

        // 1) Live phone→dedicated RX: password='Dedik', room=roster (illusion → one nick).
        if (!string.IsNullOrEmpty(roster))
        {
            list.Add(new JoinCandidate(
                roster,
                GameMatchHost.LanCreateRoomPasswordKey,
                "live LAN: fuy.password=Dedik (UI may say room name; wire=password); " +
                "room=participant roster / probe nick (cwgt)"));
        }

        // 2) Empty room + Dedik (if bfqy unset / host ignores room).
        list.Add(new JoinCandidate(
            "",
            GameMatchHost.LanCreateRoomPasswordKey,
            "Dedik password with empty room field"));

        // 3) LobbyId as room + Dedik (historical bfqz(LobbyId) path — dict still Dedik).
        if (!string.IsNullOrEmpty(id) &&
            !string.Equals(id, roster, StringComparison.Ordinal))
        {
            list.Add(new JoinCandidate(
                id,
                GameMatchHost.LanCreateRoomPasswordKey,
                "fallback: room=LobbyId + password=Dedik (old bfqz seed)"));
        }

        // 4) Older bfsq assumption (empty password) — phone hosts that still use "".
        if (!string.IsNullOrEmpty(roster))
        {
            list.Add(new JoinCandidate(
                roster,
                "",
                "fallback: bfsq-style empty password + room=roster"));
        }
        if (!string.IsNullOrEmpty(id))
        {
            list.Add(new JoinCandidate(
                id,
                "",
                "fallback: bfsq-style empty password + room=LobbyId"));
        }
        list.Add(new JoinCandidate(
            "",
            "",
            "fallback: empty room + empty password"));

        // 5) Legacy LobbyId-as-password.
        if (!string.IsNullOrEmpty(id))
        {
            list.Add(new JoinCandidate(
                id,
                id,
                "legacy: password=LobbyId (bfss-style)"));
        }

        // 6) Online-style named room: bdp.otz = GameModeDefinition.name + sep + id.
        if (!string.IsNullOrEmpty(id) && !string.IsNullOrEmpty(gameModeId))
        {
            var otzNoSep = gameModeId + id;
            list.Add(new JoinCandidate(
                otzNoSep,
                otzNoSep,
                $"bdp.otz-ish without proven separator: GameModeId+LobbyId='{otzNoSep}'"));
        }

        return list.ToArray();
    }

    /// <summary>
    /// Connect to <paramref name="host"/>; on ConnectionFailed, optionally retry
    /// <paramref name="fallback"/> (lobby LAN IP + op9 port).
    /// </summary>
    public void Connect(IPEndPoint host, IPEndPoint? fallback = null)
    {
        Endpoint = host;
        _fallback = fallback;
        _triedFallback = false;
        var keyBytes = Encoding.UTF8.GetByteCount(ConnectionKey);
        Console.WriteLine(
            $"[match-net] Connecting to game host {host.Address}:{host.Port} " +
            $"key='{ConnectionKey}' (utf8={keyBytes}, wire Put len={2 + keyBytes}) " +
            $"appId='{_appId}' userId='{_userId}' " +
            $"roster='{_rosterRoom ?? "(unset)"}' lobbyId='{_lobbyId ?? "(unset)"}' " +
            $"gameMode='{_gameModeId ?? "(unset)"}' joinCandidates={_joinCandidates.Length} …");
        if (_joinCandidates.Length > 0)
        {
            var c0 = _joinCandidates[0];
            Console.WriteLine(
                $"[match-net] JoinRoom primary: room={MatchRoomField.Describe(c0.Room)} " +
                $"password='{c0.Password}' mode=JoinOnly ({c0.Why})");
        }
        if (fallback is not null)
            Console.WriteLine($"[match-net] fallback ready: {fallback.Address}:{fallback.Port}");
        _manager.Connect(host, ConnectionKey);
    }

    private void TryFallbackAfterFailure(DisconnectReason reason)
    {
        if (_triedFallback || _fallback is null)
            return;
        if (reason is not (DisconnectReason.ConnectionFailed or DisconnectReason.Timeout))
            return;

        _triedFallback = true;
        var fb = _fallback;
        Console.WriteLine(
            $"[match-net] op9 advertised {Endpoint?.Address}; using lobby LAN {fb.Address} for match " +
            $"(PC VPN may break routing to non-LAN) :{fb.Port}");
        Endpoint = fb;
        _manager.Connect(fb, ConnectionKey);
    }

    private void StartBound()
    {
        bool ok;
        if (_bindAddress is not null)
        {
            ok = _manager.Start(_bindAddress, IPAddress.IPv6Any, 0);
            var ifName = LanInterfacePicker.FindInterfaceName(_bindAddress) ?? "?";
            Console.WriteLine(
                ok
                    ? $"[match-net] LiteNetLib bound LAN {ifName} {_bindAddress}:{_manager.LocalPort} (skip VPN default route)"
                    : $"[match-net] failed to bind LAN {_bindAddress} — falling back to Start()");
            if (!ok)
                ok = _manager.Start();
        }
        else
        {
            Console.WriteLine("[match-net] no LAN NIC for bind — Start() on default");
            ok = _manager.Start();
        }

        if (!ok)
            throw new InvalidOperationException("Failed to start LiteNetLib match client");
    }

    private async Task PollLoop()
    {
        while (!_cts.IsCancellationRequested)
        {
            _manager.PollEvents();
            MaybeLogIdle();
            try { await Task.Delay(15, _cts.Token); }
            catch (OperationCanceledException) { break; }
        }
    }

    private void MaybeLogIdle()
    {
        var peer = _peer;
        if (peer is null || peer.ConnectionState != ConnectionState.Connected)
            return;
        var now = DateTime.UtcNow;
        if (now - _lastIdleLogUtc < IdleLogInterval)
            return;
        _lastIdleLogUtc = now;
        var idle = now - _lastRxUtc;
        Console.WriteLine(
            $"[match-net] still connected {peer.Address}:{peer.Port} " +
            $"joinOk={_joinSucceeded} joinAttempts={_joinAttempts} " +
            $"actor={_localActorNr?.ToString() ?? "-"} spectatorSent={_spectatorSent} " +
            $"rx={_rxLogged} postJoinRx={_postJoinRx} idle={idle.TotalSeconds:0.0}s " +
            $"(post-Found: capture RX; probe TX team=Spectator when bootstrap ready)");
    }

    /// <summary>
    /// <c>dwu</c> on transport connected: <c>fuv(1024, clgc=AppId, userId)</c> via <c>bfxt</c>
    /// with <c>gcv.Reliable</c> → LiteNetLib <c>ReliableOrdered</c>.
    /// </summary>
    private void SendHandshake(NetPeer peer)
    {
        if (_handshakeSent) return;
        _handshakeSent = true;
        var payload = MatchCodec.BuildHandshakeRequest(_appId, _userId);
        peer.Send(payload, DeliveryMethod.ReliableOrdered);
        var n = Interlocked.Increment(ref _txLogged);
        Console.WriteLine(
            $"[match-net] TX#{n} HandshakeRequest (fuo=0) appId='{_appId}' userId='{_userId}' " +
            $"proto={MatchAuth.ProtocolVersion} len={payload.Length} " +
            $"head={Convert.ToHexString(payload.AsSpan(0, Math.Min(payload.Length, 48)))}");
        DumpCapture($"match_tx{n:D3}_HandshakeRequest", payload);
    }

    private void SendJoinRoom(NetPeer peer, string reason)
    {
        if (_joinSucceeded || _joinInFlight)
            return;
        if (_joinCandidates.Length == 0)
        {
            Console.WriteLine(
                "[match-net] Handshake OK but no JoinRoom candidates — " +
                "need roster nick (--name) and/or LobbyId from lobby JoinResponse");
            return;
        }

        if (_joinAttempts >= MaxJoinAttempts)
        {
            Console.WriteLine(
                $"[match-net] JoinRoom gave up after {_joinAttempts} attempts — " +
                "staying connected to log any late RX");
            return;
        }

        _joinInFlight = true;
        var candidate = _joinCandidates[_joinAttempts % _joinCandidates.Length];
        _joinAttempts++;
        // Host looks up by fuy.password (cwgv); dedicated LAN key is Dedik.
        var payload = MatchCodec.BuildJoinRoomRequest(
            candidate.Room, JoinRoomMode.JoinOnly, candidate.Password);
        peer.Send(payload, DeliveryMethod.ReliableOrdered);
        var n = Interlocked.Increment(ref _txLogged);
        var pwdLabel = candidate.Password.Length == 0 ? "(empty)" : $"'{candidate.Password}'";
        Console.WriteLine(
            $"[match-net] TX#{n} JoinRoomRequest (fuo=10) room='{candidate.Room}' " +
            $"mode=JoinOnly password={pwdLabel} attempt={_joinAttempts}/{MaxJoinAttempts} " +
            $"candidate={((_joinAttempts - 1) % _joinCandidates.Length) + 1}/{_joinCandidates.Length} " +
            $"({reason}) why={candidate.Why} " +
            $"len={payload.Length} head={Convert.ToHexString(payload.AsSpan(0, Math.Min(payload.Length, 48)))}");
        DumpCapture($"match_tx{n:D3}_JoinRoomRequest", payload);
    }

    private void ScheduleJoinRetry(NetPeer peer, string reason)
    {
        _joinInFlight = false;
        if (_joinSucceeded || _joinAttempts >= MaxJoinAttempts)
            return;
        var attempt = _joinAttempts;
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(JoinRetryDelay, _cts.Token);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            if (_cts.IsCancellationRequested || _joinSucceeded)
                return;
            if (!ReferenceEquals(_peer, peer) || peer.ConnectionState != ConnectionState.Connected)
                return;
            Console.WriteLine(
                $"[match-net] retry JoinRoom after {JoinRetryDelay.TotalMilliseconds:0}ms " +
                $"(prior attempt={attempt}, {reason})");
            SendJoinRoom(peer, reason);
        });
    }

    private void OnReceive(NetPeer peer, NetPacketReader reader, byte channel, DeliveryMethod method)
    {
        var len = reader.AvailableBytes;
        var payload = reader.GetRemainingBytes();
        _lastRxUtc = DateTime.UtcNow;
        var head = Convert.ToHexString(payload.AsSpan(0, Math.Min(payload.Length, 48)));
        var n = Interlocked.Increment(ref _rxLogged);
        var parsed = MatchCodec.TryParseEnvelope(
            payload, out var flags, out var opcode, out var serverTime, out var bodyOffset);
        var opLabel = parsed ? MatchCodec.OpcodeName(opcode) : "?";
        var noisy = parsed && opcode == MatchOpcode.WorldObjectState;
        var logDetail = !noisy || AllowNoisyLog();

        if (logDetail)
        {
            Console.WriteLine(
                $"[match-net] RX#{n} from {peer.Address}:{peer.Port} ch={channel} method={method} " +
                $"len={len} flags={flags}{(serverTime is int st ? $" serverTime={st}" : "")} " +
                $"op={opLabel} head={head}");
        }
        if (len == 0)
            Console.WriteLine("[match-net] empty RX (LiteNetLib keepalive / zero-payload)");
        DumpCapture($"match_rx{n:D3}_{opLabel}", payload, opcode: parsed ? opcode : null);

        if (!parsed) return;
        try
        {
            HandleMatchPayload(peer, payload, flags, opcode, bodyOffset, logDetail);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[match-net] decode error: {ex.Message}");
        }
    }

    private void HandleMatchPayload(
        NetPeer peer,
        byte[] payload,
        MatchFrameFlags flags,
        MatchOpcode opcode,
        int bodyOffset,
        bool logDetail)
    {
        switch (opcode)
        {
            case MatchOpcode.HandshakeResponse:
            {
                var r = new LobbyReader(payload.AsSpan(bodyOffset));
                var result = MatchCodec.ParseHandshakeResponseBody(r);
                Console.WriteLine(
                    $"[match-net] HandshakeResponse result={result} ({(byte)result}) flags={flags}");
                if (result == HandshakeResult.Success)
                    SendJoinRoom(peer, "after HandshakeResponse Success");
                else
                    Console.WriteLine("[match-net] handshake failed — not sending JoinRoom");
                break;
            }
            case MatchOpcode.JoinRoomResponse:
            {
                _joinInFlight = false;
                var r = new LobbyReader(payload.AsSpan(bodyOffset));
                var body = MatchCodec.ParseJoinRoomResponseBody(r);
                var name = MatchCodec.JoinRoomResultName(body.Result);
                Console.WriteLine(
                    $"[match-net] JoinRoomResponse result={name} ({(byte)body.Result}) " +
                    $"actor={(body.ActorNr?.ToString() ?? "-")} " +
                    $"msg='{body.Message ?? ""}' debug='{body.DebugMessage ?? ""}' " +
                    $"hasProps={body.HasRoomProperties} bodyLen={payload.Length - bodyOffset}");

                if (body.IsSuccess)
                {
                    _joinSucceeded = true;
                    _localActorNr = body.ActorNr;
                    Console.WriteLine(
                        "[match-net] JoinRoom Found — staying connected; logging ALL further " +
                        "match RX/TX (ActorJoined=50, CreateWorldObject=200, WorldObjectRpc=203, …). " +
                        "WorldObjectState rate-limited (first " + NoisyLogFirst +
                        $" then every {NoisyLogEveryK}th / summary). " +
                        "Probe will TX SetProperty team=Spectator (cux=3) after host bootstrap " +
                        $"(C2=10) or {SpectatorFallbackDelay.TotalSeconds:0.#}s fallback — " +
                        "phone OBT UI may hide Spectator; wire still has it. " +
                        $"localActor={body.ActorNr?.ToString() ?? "(missing)"}");
                    ScheduleSpectatorFallback(peer);
                    break;
                }

                if (body.Result == JoinRoomResult.RoomNotFound)
                {
                    var next = _joinAttempts < MaxJoinAttempts && _joinCandidates.Length > 0
                        ? _joinCandidates[_joinAttempts % _joinCandidates.Length]
                        : default;
                    Console.WriteLine(
                        "[match-net] RoomNotFound — host dict miss on fuy.password " +
                        "(dedicated LAN expects Dedik; op9 can also precede CreateRoom). " +
                        (next.Why is { Length: > 0 }
                            ? $"next candidate room='{next.Room}' password='{next.Password}'"
                            : "no more candidates"));
                    ScheduleJoinRetry(peer, "RoomNotFound");
                    break;
                }

                if (body.Result == JoinRoomResult.InvalidCreateOptions)
                {
                    Console.WriteLine(
                        "[match-net] InvalidCreateOptions — usually JoinOrCreate/CreateOnly with null " +
                        "fzf; LAN bfso uses JoinOnly. Not inventing createOptions.");
                    break;
                }

                Console.WriteLine(
                    $"[match-net] JoinRoom failed ({name}) — staying connected to log further RX");
                break;
            }
            case MatchOpcode.SetProperties:
            {
                if (_joinSucceeded)
                    Interlocked.Increment(ref _postJoinRx);
                TryNoteBootstrapFromSetProperties(payload, bodyOffset, flags);
                if (logDetail)
                    LogPostFoundOpcode(opcode, flags, payload, bodyOffset);
                TrySendSpectator(peer, "after SetProperties");
                break;
            }
            case MatchOpcode.SetProperty:
            {
                if (_joinSucceeded)
                    Interlocked.Increment(ref _postJoinRx);
                if (logDetail)
                    LogPostFoundOpcode(opcode, flags, payload, bodyOffset);
                // Money often lands just before team assign in phone-host capture.
                TrySendSpectator(peer, "after SetProperty");
                break;
            }
            case MatchOpcode.CreateWorldObject:
            {
                if (_joinSucceeded)
                    Interlocked.Increment(ref _postJoinRx);
                _sceneManagersSeen++;
                // Phone-host bootstrap: 8 scene managers then C2=10. Count alone is a hint.
                if (_sceneManagersSeen >= MatchSceneManagers.Bootstrap.Length)
                    MarkBootstrapReady("scene managers");
                if (logDetail)
                    LogPostFoundOpcode(opcode, flags, payload, bodyOffset);
                TrySendSpectator(peer, "after CreateWorldObject");
                break;
            }
            default:
            {
                if (_joinSucceeded)
                    Interlocked.Increment(ref _postJoinRx);
                if (!logDetail)
                    break;
                LogPostFoundOpcode(opcode, flags, payload, bodyOffset);
                break;
            }
        }
    }

    private void LogPostFoundOpcode(
        MatchOpcode opcode,
        MatchFrameFlags flags,
        byte[] payload,
        int bodyOffset)
    {
        var bodyLen = payload.Length - bodyOffset;
        var known = Enum.IsDefined(typeof(MatchOpcode), opcode)
            && !string.Equals(MatchCodec.OpcodeName(opcode), $"op{(byte)opcode}", StringComparison.Ordinal);
        var kind = MatchCodec.IsPostJoinTraffic(opcode)
            ? "POST-FOUND"
            : (known ? "known-op" : "UNKNOWN-op");
        var dumpLen = Math.Min(payload.Length, 96);
        var hex = Convert.ToHexString(payload.AsSpan(0, dumpLen));
        Console.WriteLine(
            $"[match-net] {kind} op={MatchCodec.OpcodeName(opcode)} ({(byte)opcode}) " +
            $"flags={flags} bodyLen={bodyLen} " +
            (bodyLen > 0
                ? $"hex={hex}{(payload.Length > dumpLen ? "…" : "")}"
                : "(header only)"));
        if (!known)
            Console.WriteLine(
                "[match-net] opcode not in fuo DiffableCs map — keep bin capture; " +
                "do not invent decode");
    }

    private void TryNoteBootstrapFromSetProperties(
        byte[] payload,
        int bodyOffset,
        MatchFrameFlags flags)
    {
        if ((flags & (MatchFrameFlags.IsEncrypted | MatchFrameFlags.IsCompressed)) != 0)
            return;
        try
        {
            var r = new LobbyReader(payload.AsSpan(bodyOffset));
            var (actorNr, props) = MatchCodec.ParseSetPropertiesBody(r);
            // Phone-host: actor=0 C2=10 unlocks InitWaiting — same gate before team assign.
            if (actorNr != 0)
                return;
            foreach (var (key, value) in props)
            {
                if (key == MatchRoomPropKeys.C2
                    && value.Kind == LobbyVariantKind.Byte
                    && value.Byte == MatchHostActor.C2AfterManagers)
                {
                    MarkBootstrapReady($"room C2={value.Byte}");
                    return;
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[match-net] SetProperties bootstrap parse: {ex.Message}");
        }
    }

    private void MarkBootstrapReady(string reason)
    {
        if (_bootstrapReady)
            return;
        _bootstrapReady = true;
        Console.WriteLine($"[match-net] bootstrap ready ({reason}) — probe may TX team=Spectator");
    }

    private void ScheduleSpectatorFallback(NetPeer peer)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(SpectatorFallbackDelay, _cts.Token);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            if (!_bootstrapReady)
                MarkBootstrapReady($"fallback {SpectatorFallbackDelay.TotalSeconds:0.#}s after Found");
            TrySendSpectator(peer, "fallback timer");
        });
    }

    /// <summary>
    /// ConnectAsClient is not a UI client — after Found + bootstrap, self-assign
    /// <c>team=Spectator (3)</c> via client SetProperty (key <c>team</c>, fzq.Byte <c>FF03</c>).
    /// OBT phone UI may hide Spectator; wire/protocol still has it (cux).
    /// </summary>
    private void TrySendSpectator(NetPeer peer, string reason)
    {
        if (_spectatorSent || !_joinSucceeded || !_bootstrapReady)
            return;
        if (_localActorNr is not { } actor)
        {
            Console.WriteLine(
                "[match-net] probe → Spectator deferred: Found had no actorNr");
            return;
        }

        if (peer.ConnectionState != ConnectionState.Connected)
            return;

        _spectatorSent = true;
        var payload = MatchCodec.BuildClientSetProperty(
            actor,
            MatchRoomPropKeys.Team,
            LobbyVariant.FromByte((byte)MatchTeam.Spectator));
        peer.Send(payload, DeliveryMethod.ReliableOrdered);
        var n = Interlocked.Increment(ref _txLogged);
        Console.WriteLine(
            $"[match-net] probe → Spectator (cux=3) actor={actor} via SetProperty team FF03 " +
            $"(reason={reason}) TX#{n} len={payload.Length} " +
            $"hex={Convert.ToHexString(payload)}");
        DumpCapture($"match_tx{n:D3}_SetProperty_team_Spectator", payload, MatchOpcode.SetProperty);
    }

    /// <summary>
    /// WorldObjectState flood: full log first N, then every Kth, else 1-line summary every few seconds.
    /// </summary>
    private bool AllowNoisyLog()
    {
        var n = Interlocked.Increment(ref _wosSeen);
        if (n <= NoisyLogFirst || n % NoisyLogEveryK == 0)
            return true;

        Interlocked.Increment(ref _wosSuppressed);
        var now = DateTime.UtcNow;
        if (now - _lastWosSummaryUtc >= NoisySummaryInterval)
        {
            _lastWosSummaryUtc = now;
            var suppressed = Interlocked.Exchange(ref _wosSuppressed, 0);
            Console.WriteLine(
                $"[match-net] WorldObjectState summary: seen={n} suppressed≈{suppressed} " +
                $"(logging first {NoisyLogFirst} + every {NoisyLogEveryK}th)");
        }
        return false;
    }

    private void DumpCapture(string tag, byte[] payload, MatchOpcode? opcode = null)
    {
        if (!_saveCaptures) return;
        if (opcode == MatchOpcode.WorldObjectState)
        {
            var c = Interlocked.Increment(ref _wosCaptures);
            if (c > MaxWorldObjectStateCaptures)
            {
                if (c == MaxWorldObjectStateCaptures + 1)
                    Console.WriteLine(
                        $"[match-net] WorldObjectState captures capped at {MaxWorldObjectStateCaptures}");
                return;
            }
        }
        try
        {
            var i = Interlocked.Increment(ref _captureIndex);
            var path = Path.Combine(_captureDir, $"{DateTime.UtcNow:yyyyMMdd_HHmmss}_{i:D3}_{tag}_len{payload.Length}.bin");
            File.WriteAllBytes(path, payload);
            Console.WriteLine($"[match-net] captured {path}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[match-net] capture failed: {ex.Message}");
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        try { Task.WhenAll(_tasks).Wait(800); } catch { /* ignore */ }
        _manager.Stop();
        _cts.Dispose();
    }
}

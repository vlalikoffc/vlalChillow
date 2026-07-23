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
    /// Spectator-only: if host bootstrap (C2=10) is slow/missing, still TX team=Spectator after this
    /// delay. Fighting probes do NOT use a timer — they wait for a real peer pawn (see
    /// <see cref="TryDetectPeerFighterFromCwo"/>).
    /// </summary>
    private static readonly TimeSpan TeamAssignFallbackDelay = TimeSpan.FromSeconds(2.5);
    /// <summary>WorldObjectState tick while probe is a fighting pawn (killable).</summary>
    private static readonly TimeSpan FightingStateInterval = TimeSpan.FromMilliseconds(100);

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
    private bool _probeTeamSent;
    private bool _probeSpawned;
    private short _probePawnObjectId;
    private byte _probePawnViewSeq;
    private uint _probeStateSeq;
    private CancellationTokenSource? _probeStateCts;
    private int _sceneManagersSeen;
    // Fighter probe waits for a REAL peer pawn on the same team, then copies its spawn pose.
    private readonly HashSet<short> _peerFighterPawnIds = new();
    private short _activePeerPawnId;
    private byte[]? _lastPeerCwoTrailing;
    private bool _probeRespawnPending;
    private byte _roomC2;
    private (float X, float Y, float Z) _probeSpawnPos;
    private (float X, float Y, float Z, float W) _probeSpawnQuat;
    private bool _probeSpawnPosKnown;
    private readonly MatchTeam _probeTeam;
    private readonly MatchProbeDecode _decode = new();
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
        string? rosterRoom = null,
        MatchTeam probeTeam = MatchTeam.Spectator)
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
        _probeTeam = probeTeam is MatchTeam.Tr or MatchTeam.Ct or MatchTeam.Spectator
            ? probeTeam
            : MatchTeam.Spectator;
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
            _probeTeamSent = false;
            _probeSpawned = false;
            _probePawnObjectId = 0;
            _probePawnViewSeq = 0;
            _probeStateSeq = 0;
            StopProbeStateLoop();
            _sceneManagersSeen = 0;
            _peerFighterPawnIds.Clear();
            _activePeerPawnId = 0;
            _lastPeerCwoTrailing = null;
            _probeRespawnPending = false;
            _roomC2 = 0;
            _probeSpawnPosKnown = false;
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
            $"actor={_localActorNr?.ToString() ?? "-"} teamSent={_probeTeamSent} spawned={_probeSpawned} " +
            $"probeTeam={_probeTeam} peerFighters={_peerFighterPawnIds.Count} " +
            $"rx={_rxLogged} postJoinRx={_postJoinRx} idle={idle.TotalSeconds:0.0}s " +
            $"(fighter probe spawns only when a peer {_probeTeam} pawn appears)");
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

    /// <summary>Human decode roster (actors + objectId→owner). For console <c>roster</c>.</summary>
    public string DescribeRoster() => _decode.FormatRoster();

    private void OnReceive(NetPeer peer, NetPacketReader reader, byte channel, DeliveryMethod method)
    {
        var len = reader.AvailableBytes;
        var payload = reader.GetRemainingBytes();
        _lastRxUtc = DateTime.UtcNow;
        var n = Interlocked.Increment(ref _rxLogged);
        var parsed = MatchCodec.TryParseEnvelope(
            payload, out var flags, out var opcode, out var serverTime, out var bodyOffset);
        var opLabel = parsed ? MatchCodec.OpcodeName(opcode) : "?";
        var noisy = parsed && opcode == MatchOpcode.WorldObjectState;
        var logDetail = !noisy || AllowNoisyLog();

        if (len == 0)
            Console.WriteLine("[match-net] empty RX (LiteNetLib keepalive / zero-payload)");

        DumpCapture($"match_rx{n:D3}_{opLabel}", payload, opcode: parsed ? opcode : null);

        if (!parsed)
        {
            Console.WriteLine(
                $"[match-net] RX#{n} UNPARSED envelope len={len} " +
                $"head={Convert.ToHexString(payload.AsSpan(0, Math.Min(payload.Length, 48)))}");
            return;
        }

        try
        {
            if (logDetail)
            {
                var decoded = _decode.FormatPacket(
                    "RX", n, flags, opcode, serverTime, payload, bodyOffset, includeNoisyDetail: true);
                if (!string.IsNullOrEmpty(decoded))
                    Console.WriteLine(decoded);
            }
            else if (noisy)
            {
                // Rate-limited WOS: still update object map quietly when possible.
                TryNoteStateObjectQuiet(payload, bodyOffset);
            }

            HandleMatchPayload(peer, payload, flags, opcode, bodyOffset, logDetail);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[match-net] decode error: {ex.Message}");
        }
    }

    private void TryNoteStateObjectQuiet(byte[] payload, int bodyOffset)
    {
        try
        {
            if (!MatchCodec.TryOpenBody(payload, out _, out _, out _, out var body))
                return;
            var r = new LobbyReader(body);
            if (r.Remaining < 2) return;
            _ = r.ReadInt16(); // id — map already known from CreateWorldObject
        }
        catch { /* ignore */ }
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
                // Human decode already printed by FormatPacket.
                var r = new LobbyReader(payload.AsSpan(bodyOffset));
                var result = MatchCodec.ParseHandshakeResponseBody(r);
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

                if (body.IsSuccess)
                {
                    _joinSucceeded = true;
                    _localActorNr = body.ActorNr;
                    _decode.SetLocalActor(body.ActorNr);
                    if (body.ActorNr is { } self)
                        _decode.NoteActor(self, _rosterRoom ?? "probe");
                    Console.WriteLine(
                        "[match-net] JoinRoom Found — decode tags: MATCH=room/C2/Time/Score, " +
                        "PLAYER=actor props + pawn State/Rpc, SCENE=managers. " +
                        $"WorldObjectState rate-limited (first {NoisyLogFirst} + every {NoisyLogEveryK}th). " +
                        $"probeTeam={_probeTeam} localActor={body.ActorNr?.ToString() ?? "(missing)"}");
                    Console.WriteLine(_decode.FormatRoster());
                    if (_probeTeam is MatchTeam.Ct or MatchTeam.Tr)
                        Console.WriteLine(
                            $"[probe] waiting for peer {_probeTeam}… (will self-assign team + spawn a " +
                            $"'{MatchCodec.PawnTypeNameForTeam(_probeTeam)}' pawn AT the first real peer " +
                            "fighter's position — no blind bootstrap-timer spawn)");
                    else
                        // Spectator has no pawn/visibility concern — assign team once bootstrap is ready.
                        ScheduleSpectatorTeamAssign(peer);
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
                TryNoteRoomPropsFromSetProperties(peer, payload, bodyOffset, flags);
                TrySendSpectatorTeam(peer, "after SetProperties");
                break;
            }
            case MatchOpcode.SetProperty:
            {
                if (_joinSucceeded)
                    Interlocked.Increment(ref _postJoinRx);
                TrySendSpectatorTeam(peer, "after SetProperty");
                break;
            }
            case MatchOpcode.CreateWorldObject:
            {
                if (_joinSucceeded)
                    Interlocked.Increment(ref _postJoinRx);
                _sceneManagersSeen++;
                if (_sceneManagersSeen >= MatchSceneManagers.Bootstrap.Length)
                    MarkBootstrapReady("scene managers");
                // Fighter probe: detect a REAL peer pawn on our team + copy its spawn pose.
                TryDetectPeerFighterFromCwo(peer, payload);
                TrySendSpectatorTeam(peer, "after CreateWorldObject");
                break;
            }
            case MatchOpcode.WorldObjectState:
            {
                if (_joinSucceeded)
                    Interlocked.Increment(ref _postJoinRx);
                // Fallback pose source when a peer fighter's CWO trailing had no parseable pose.
                TryDetectPeerPositionFromState(peer, payload);
                break;
            }
            case MatchOpcode.DestroyWorldObject:
            {
                if (_joinSucceeded)
                    Interlocked.Increment(ref _postJoinRx);
                TryHandlePeerPawnDestroy(peer, payload);
                break;
            }
            case MatchOpcode.ActorJoinedEvent:
            case MatchOpcode.ActorLeftEvent:
            case MatchOpcode.WorldObjectRpc:
            case MatchOpcode.SetInternalProperty:
            case MatchOpcode.FetchServerTimeRequest:
            case MatchOpcode.FetchServerTimeResponse:
            {
                if (_joinSucceeded)
                    Interlocked.Increment(ref _postJoinRx);
                break;
            }
            default:
            {
                if (_joinSucceeded)
                    Interlocked.Increment(ref _postJoinRx);
                if (logDetail)
                {
                    // Unknown ops: FormatPacket already noted undecoded; keep short hex tail.
                    var dumpLen = Math.Min(payload.Length, 64);
                    Console.WriteLine(
                        $"[match-net]   hex={Convert.ToHexString(payload.AsSpan(0, dumpLen))}" +
                        (payload.Length > dumpLen ? "…" : ""));
                }
                break;
            }
        }
    }

    private void TryNoteRoomPropsFromSetProperties(
        NetPeer peer,
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
                if (key != MatchRoomPropKeys.C2 || value.Kind != LobbyVariantKind.Byte)
                    continue;
                if (value.Byte == MatchHostActor.C2AfterManagers)
                    MarkBootstrapReady($"room C2={value.Byte}");
                TryHandleProbeRoomC2(peer, value.Byte);
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[match-net] SetProperties room parse: {ex.Message}");
        }
    }

    /// <summary>
    /// Phase-aware probe respawn. Gold TDM: freeforall pawn must not survive into WarmUp/Live —
    /// real clients Destroy+Create (385→386→387) while a stale probe id=257 stayed orphaned
    /// ("alive" on team, no model/radar).
    /// <list type="bullet">
    /// <item>WarmUp(21): peers often keep the same pawn id — force Destroy+Create with a fresh
    /// view id at the last peer pose.</item>
    /// <item>Live(30): peers Destroy+recreate — do <b>not</b> Destroy here (races when peer Create
    /// already arrived before this C2 bag). Follow <see cref="TryHandlePeerPawnDestroy"/> +
    /// <see cref="TryDetectPeerFighterFromCwo"/> only.</item>
    /// </list>
    /// </summary>
    private void TryHandleProbeRoomC2(NetPeer peer, byte newC2)
    {
        if (_probeTeam is not (MatchTeam.Ct or MatchTeam.Tr))
            return;
        if (newC2 == _roomC2)
            return;

        var prev = _roomC2;
        _roomC2 = newC2;

        if (newC2 != MatchC2States.WarmUp && newC2 != MatchC2States.DeathMatchLive)
            return;

        if (newC2 == MatchC2States.DeathMatchLive)
        {
            // Peer Destroy+Create owns Live respawn. Setting pending+Destroy here orphaned the probe
            // when peer Create raced ahead of the C2=30 SetProperties bag.
            Console.WriteLine(
                $"[probe] phase C2 {prev}→{newC2} — await peer pawn Destroy+Create " +
                $"(probe id={(_probeSpawned ? _probePawnObjectId.ToString() : "none")}, " +
                $"peer id={_activePeerPawnId})");
            return;
        }

        // WarmUp: force a fresh view id even when the peer keeps the same object id.
        if (!_probeSpawned)
        {
            _probeRespawnPending = true;
            Console.WriteLine(
                $"[probe] phase C2 {prev}→{newC2} — not spawned yet, will spawn on peer CWO");
            return;
        }

        Console.WriteLine(
            $"[probe] phase C2 {prev}→{newC2} — destroying pawn id={_probePawnObjectId} for WarmUp respawn");
        DestroyProbePawn(peer, $"phase C2={newC2}");
        _probeRespawnPending = true;

        if (_probeSpawnPosKnown)
        {
            SpawnOrRespawnProbeAtPeer(
                peer,
                null,
                _probeSpawnPos.X, _probeSpawnPos.Y, _probeSpawnPos.Z,
                _probeSpawnQuat.X, _probeSpawnQuat.Y, _probeSpawnQuat.Z, _probeSpawnQuat.W,
                _lastPeerCwoTrailing,
                $"phase C2={newC2} at last peer pose (peer id={_activePeerPawnId})");
        }
    }

    /// <summary>
    /// When a peer same-team fighter pawn is destroyed (phase respawn or combat death on host),
    /// destroy our probe pawn too so the next peer CreateWorldObject triggers a fresh spawn.
    /// </summary>
    private void TryHandlePeerPawnDestroy(NetPeer peer, byte[] payload)
    {
        if (_probeTeam is not (MatchTeam.Ct or MatchTeam.Tr) || !_joinSucceeded)
            return;
        try
        {
            if (!MatchCodec.TryOpenBody(payload, out _, out var opcode, out _, out var body)
                || opcode != MatchOpcode.DestroyWorldObject)
                return;
            var id = MatchCodec.ParseDestroyWorldObjectBody(new LobbyReader(body));
            if (id != _activePeerPawnId && !_peerFighterPawnIds.Contains(id))
                return;

            Console.WriteLine(
                $"[probe] peer pawn Destroy id={id} — destroying probe for respawn " +
                $"(active peer id={_activePeerPawnId})");
            _peerFighterPawnIds.Remove(id);
            if (id == _activePeerPawnId)
                _activePeerPawnId = 0;

            if (_probeSpawned)
                DestroyProbePawn(peer, $"peer pawn Destroy id={id}");
            _probeRespawnPending = true;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[probe] peer Destroy parse: {ex.Message}");
        }
    }

    private void StopProbeStateLoop()
    {
        _probeStateCts?.Cancel();
        _probeStateCts?.Dispose();
        _probeStateCts = null;
    }

    private void DestroyProbePawn(NetPeer peer, string reason)
    {
        if (_probePawnObjectId == 0)
            return;
        if (peer.ConnectionState != ConnectionState.Connected)
            return;

        var id = _probePawnObjectId;
        StopProbeStateLoop();
        var destroy = MatchCodec.BuildClientDestroyWorldObject(id);
        peer.Send(destroy, DeliveryMethod.ReliableOrdered);
        var n = Interlocked.Increment(ref _txLogged);
        Console.WriteLine($"[probe] DestroyWorldObject id={id} (reason={reason})");
        DumpCapture($"match_tx{n:D3}_DestroyWorldObject_probe_{id}", destroy, MatchOpcode.DestroyWorldObject);

        _probePawnObjectId = 0;
        _probeSpawned = false;
        _probeStateSeq = 0;
    }

    private short AllocateProbeViewId(byte actor)
    {
        _probePawnViewSeq++;
        return (short)((actor << 7) | _probePawnViewSeq);
    }

    private void MarkBootstrapReady(string reason)
    {
        if (_bootstrapReady)
            return;
        _bootstrapReady = true;
        Console.WriteLine($"[match-net] bootstrap ready ({reason}) — probe may TX team={_probeTeam}");
    }

    private void ScheduleSpectatorTeamAssign(NetPeer peer)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(TeamAssignFallbackDelay, _cts.Token);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            if (!_bootstrapReady)
                MarkBootstrapReady($"fallback {TeamAssignFallbackDelay.TotalSeconds:0.#}s after Found");
            TrySendSpectatorTeam(peer, "fallback timer");
        });
    }

    /// <summary>
    /// Spectator-only self-assign: after Found + bootstrap, TX <c>team=Spectator</c>. Spectators
    /// have no pawn/model, so there is nothing to spawn and no peer to wait for. Fighting teams
    /// go through <see cref="TryDetectPeerFighterFromCwo"/> → <see cref="SpawnOrRespawnProbeAtPeer"/> instead.
    /// </summary>
    private void TrySendSpectatorTeam(NetPeer peer, string reason)
    {
        if (_probeTeam is MatchTeam.Ct or MatchTeam.Tr)
            return; // fighters spawn only when a real peer pawn appears
        if (_probeTeamSent || !_joinSucceeded || !_bootstrapReady)
            return;
        if (_localActorNr is not { } actor)
        {
            Console.WriteLine("[match-net] probe → Spectator deferred: Found had no actorNr");
            return;
        }
        if (peer.ConnectionState != ConnectionState.Connected)
            return;

        _probeTeamSent = true;
        SendProbeIdentityAndTeam(peer, actor, reason);
        Console.WriteLine("[probe] team=Spectator is non-fighting — no pawn/State");
    }

    /// <summary>
    /// Fighter probe trigger: when a REAL peer's fighting pawn (<c>Ct_Ct</c>/<c>Tr_Tr</c>) for the
    /// team we want appears via <c>CreateWorldObject</c>, copy its spawn pose from the CWO trailing
    /// and spawn the probe's pawn AT that position. Without a live peer fighter to anchor on, no
    /// model/radar blip ever appeared (blind bootstrap-timer spawn used the wrong-map Sandstone
    /// pose and fired before any team pawn existed). Pose comes from the peer's own CWO — not invented.
    /// </summary>
    private void TryDetectPeerFighterFromCwo(NetPeer peer, byte[] payload)
    {
        if (!_joinSucceeded)
            return;
        if (_probeTeam is not (MatchTeam.Ct or MatchTeam.Tr))
            return;
        try
        {
            if (!MatchCodec.TryOpenBody(payload, out _, out var opcode, out _, out var body)
                || opcode != MatchOpcode.CreateWorldObject)
                return;
            var r = new LobbyReader(body);
            var cwo = MatchCodec.ParseCreateWorldObjectBody(r);
            var wantType = MatchCodec.PawnTypeNameForTeam(_probeTeam);
            if (!string.Equals(cwo.TypeName, wantType, StringComparison.Ordinal))
                return;
            // Ignore our own future pawn (owner == our actor); accept any other owner.
            if (cwo.OwnerActorNr is { } owner && _localActorNr is { } self && owner == self)
                return;

            var isNewPeerPawn = cwo.ObjectId != _activePeerPawnId;
            var newPeer = _peerFighterPawnIds.Add(cwo.ObjectId);
            if (isNewPeerPawn)
            {
                _activePeerPawnId = cwo.ObjectId;
                _lastPeerCwoTrailing = cwo.Trailing.Length > 0 ? cwo.Trailing.ToArray() : null;
            }
            if (newPeer)
                Console.WriteLine(
                    $"[probe] peer {_probeTeam} pawn CWO id={cwo.ObjectId} owner=" +
                    $"{cwo.OwnerActorNr?.ToString() ?? "-"} trail={cwo.Trailing.Length}B");

            // Respawn when: first spawn, WarmUp pending, or peer Destroy+recreate (new object id).
            var needSpawn = !_probeSpawned || _probeRespawnPending || isNewPeerPawn;
            if (!needSpawn)
                return;

            if (isNewPeerPawn && _probeSpawned)
                Console.WriteLine(
                    $"[probe] peer pawn recreated id={cwo.ObjectId} — Destroy+re-Create probe " +
                    $"(was id={_probePawnObjectId})");

            if (MatchCodec.TryParsePawnSpawnTrailing(
                    cwo.Trailing,
                    out var x, out var y, out var z,
                    out var qx, out var qy, out var qz, out var qw))
            {
                SpawnOrRespawnProbeAtPeer(
                    peer, cwo.OwnerActorNr, x, y, z, qx, qy, qz, qw,
                    cwo.Trailing.Length > 0 ? cwo.Trailing.ToArray() : null,
                    $"peer CWO id={cwo.ObjectId} type={cwo.TypeName}");
            }
            else if (newPeer || _probeRespawnPending)
            {
                Console.WriteLine(
                    $"[probe] peer pawn id={cwo.ObjectId} trailing had no parseable pose — " +
                    "waiting for its WorldObjectState position");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[probe] peer CWO parse: {ex.Message}");
        }
    }

    /// <summary>
    /// Fallback pose source: if a peer fighter's CWO trailing had no pose, use the position from
    /// its first <c>WorldObjectState</c> tick. Only fires for object ids we already tagged as peer
    /// fighters on our team (see <see cref="TryDetectPeerFighterFromCwo"/>).
    /// </summary>
    private void TryDetectPeerPositionFromState(NetPeer peer, byte[] payload)
    {
        if (!_joinSucceeded)
            return;
        if (_probeTeam is not (MatchTeam.Ct or MatchTeam.Tr))
            return;
        if (_peerFighterPawnIds.Count == 0)
            return;
        if (!_probeRespawnPending && _probeSpawned)
            return;
        try
        {
            if (!MatchCodec.TryOpenBody(payload, out _, out var opcode, out _, out var body)
                || opcode != MatchOpcode.WorldObjectState)
                return;
            var r = new LobbyReader(body);
            if (r.Remaining < 2 + 4 + 4 + 4 + 4 + 12)
                return;
            var id = r.ReadInt16();
            if (!_peerFighterPawnIds.Contains(id))
                return;
            _ = r.ReadInt32(); // pad
            _ = r.ReadInt32(); // seq
            _ = r.ReadFloat(); // tick
            _ = r.ReadInt32(); // pad
            var x = r.ReadFloat();
            var y = r.ReadFloat();
            var z = r.ReadFloat();
            // Skip an all-zero (uninitialised) pose — wait for a real position tick.
            if (x == 0f && y == 0f && z == 0f)
                return;
            // No orientation in the standing-State header we decode — face the team default.
            var q = _probeTeam == MatchTeam.Ct
                ? MatchSceneManagers.PawnSpawn.SandstoneQuatCt
                : MatchSceneManagers.PawnSpawn.SandstoneQuatTr;
            SpawnOrRespawnProbeAtPeer(
                peer, null, x, y, z, q.X, q.Y, q.Z, q.W,
                _lastPeerCwoTrailing,
                $"peer State id={id}");
        }
        catch { /* ignore — State layout is partially opaque */ }
    }

    /// <summary>
    /// Fighter spawn/respawn anchored to a real peer's pose: SetProperty team (first spawn only),
    /// then owner-encoded CreateWorldObject (<c>id=(actor&lt;&lt;7)|k</c>, k increments per Create),
    /// then spawn RPC (<c>rpc=localActor</c>) + State loop. Phase transitions and peer pawn
    /// Destroy+recreate must bump k — reusing id=257 across Live left an orphaned invisible pawn.
    /// </summary>
    private void SpawnOrRespawnProbeAtPeer(
        NetPeer peer,
        byte? peerActor,
        float px, float py, float pz,
        float qx, float qy, float qz, float qw,
        byte[]? peerTrailing,
        string source)
    {
        if (!_joinSucceeded)
            return;
        if (_probeTeam is not (MatchTeam.Ct or MatchTeam.Tr))
            return;
        if (_localActorNr is not { } actor)
        {
            Console.WriteLine("[probe] peer fighter seen but Found had no actorNr — cannot spawn");
            return;
        }
        if (peer.ConnectionState != ConnectionState.Connected)
            return;

        var firstSpawn = !_probeTeamSent;
        if (_probeSpawned)
            DestroyProbePawn(peer, $"respawn before Create ({source})");

        _probeSpawned = true;
        _probeTeamSent = true;
        _probeRespawnPending = false;
        _probeSpawnPos = (px, py, pz);
        _probeSpawnQuat = (qx, qy, qz, qw);
        _probeSpawnPosKnown = true;

        Console.WriteLine(
            $"[probe] peer seen actor={peerActor?.ToString() ?? "?"} " +
            $"pos=({px:0.##},{py:0.##},{pz:0.##}) — spawning here " +
            $"(team={_probeTeam}, source={source})");

        if (firstSpawn)
            SendProbeIdentityAndTeam(peer, actor, source);

        _probePawnObjectId = AllocateProbeViewId(actor);
        var typeName = MatchCodec.PawnTypeNameForTeam(_probeTeam);
        byte[] trailing;
        if (peerTrailing is { Length: > 0 } pt
            && MatchCodec.TryParsePawnSpawnTrailing(
                pt, out _, out _, out _, out _, out _, out _, out _))
        {
            trailing = pt;
            var tagNote = MatchCodec.TryReadPawnLoadoutTag(pt, out var tag) && tag.Length > 0
                ? $" loadout='{tag}'"
                : "";
            Console.WriteLine(
                $"[probe] using peer CWO trailing verbatim ({trailing.Length}B{tagNote})");
        }
        else
        {
            trailing = MatchCodec.BuildPawnSpawnPayload(px, py, pz, qx, qy, qz, qw);
        }

        var cwo = MatchCodec.BuildClientCreateWorldObject(
            WorldObjectKind.Entity,
            _probePawnObjectId,
            typeName,
            MatchSceneManagers.PlayerPawnFusTag,
            ownerActorNr: actor,
            trailingPayload: trailing);
        peer.Send(cwo, DeliveryMethod.ReliableOrdered);
        var n = Interlocked.Increment(ref _txLogged);
        _decode.NoteObject(_probePawnObjectId, actor, typeName, WorldObjectKind.Entity);
        Console.WriteLine(
            $"[probe] CreateWorldObject id={_probePawnObjectId} (=(actor<<7)|{_probePawnViewSeq}, owner-encoded) " +
            $"type='{typeName}' owner=actor={actor} pos=({px:0.##},{py:0.##},{pz:0.##}) " +
            $"trail={trailing.Length}B head={Convert.ToHexString(cwo.AsSpan(0, Math.Min(cwo.Length, 32)))}");
        DumpCapture($"match_tx{n:D3}_CreateWorldObject_{typeName}", cwo, MatchOpcode.CreateWorldObject);

        var tickTime = Environment.TickCount / 1000.0;
        var spawnRpc = MatchCodec.BuildClientWorldObjectRpc(
            _probePawnObjectId,
            rpcId: actor,
            gaaTarget: 4,
            field: 2,
            timeValue: tickTime,
            payload: MatchCodec.BuildPawnRpcSpawnTailPayload());
        peer.Send(spawnRpc, DeliveryMethod.ReliableOrdered);
        n = Interlocked.Increment(ref _txLogged);
        Console.WriteLine(
            $"[probe] WorldObjectRpc rpc={actor} id={_probePawnObjectId} owner=actor={actor} " +
            "gaa=4 field=2 (post-create spawn RPC — rpc matches localActor like gold peers)");
        DumpCapture($"match_tx{n:D3}_WorldObjectRpc_rpc{actor}", spawnRpc, MatchOpcode.WorldObjectRpc);

        StartFightingStateLoop(peer);
    }

    /// <summary>
    /// Identity subset a real joiner authors before its pawn CWO (gold "фейк влал" RX#165–167:
    /// uid, badgeId, from_lobby) plus the <c>team</c> SetProperty. Shared by the spectator path and
    /// the fighter spawn so the host + peers register the probe as a full player, not a bare actor.
    /// </summary>
    private void SendProbeIdentityAndTeam(NetPeer peer, byte actor, string reason)
    {
        SendProbeProp(peer, actor, MatchRoomPropKeys.Uid,
            LobbyVariant.FromString(MatchHostActor.BootstrapUid), "uid");
        SendProbeProp(peer, actor, MatchRoomPropKeys.BadgeId, LobbyVariant.FromInt(0), "badgeId");
        SendProbeProp(peer, actor, MatchRoomPropKeys.FromLobby, LobbyVariant.FromBool(false), "from_lobby");

        var teamPkt = MatchCodec.BuildClientSetProperty(
            actor,
            MatchRoomPropKeys.Team,
            LobbyVariant.FromByte((byte)_probeTeam));
        peer.Send(teamPkt, DeliveryMethod.ReliableOrdered);
        var n = Interlocked.Increment(ref _txLogged);
        _decode.NoteActor(actor, _rosterRoom ?? "probe", _probeTeam);
        Console.WriteLine(
            $"[probe] team → {_probeTeam} actor={actor} ({_rosterRoom ?? "probe"}) " +
            $"(reason={reason}) — SetProperty team len={teamPkt.Length}");
        DumpCapture($"match_tx{n:D3}_SetProperty_team_{_probeTeam}", teamPkt, MatchOpcode.SetProperty);
    }

    /// <summary>
    /// Client→host SetProperty (op101, flags=None) for a single actor prop, with a <c>[probe]</c>
    /// log + capture. Used for the identity props a real joiner authors before its pawn CWO.
    /// </summary>
    private void SendProbeProp(NetPeer peer, byte actor, string key, LobbyVariant value, string label)
    {
        var pkt = MatchCodec.BuildClientSetProperty(actor, key, value);
        peer.Send(pkt, DeliveryMethod.ReliableOrdered);
        var n = Interlocked.Increment(ref _txLogged);
        Console.WriteLine($"[probe] SetProperty {label} actor={actor} len={pkt.Length}");
        DumpCapture($"match_tx{n:D3}_SetProperty_{label}", pkt, MatchOpcode.SetProperty);
    }

    private void StartFightingStateLoop(NetPeer peer)
    {
        StopProbeStateLoop();
        _probeStateCts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
        var loopCts = _probeStateCts;
        var objectId = _probePawnObjectId;
        // Peer-anchored position (copied from the real teammate we spawned next to). Falls back to
        // the Sandstone pose only if, somehow, we spawned without a known pose.
        var pos = _probeSpawnPosKnown
            ? _probeSpawnPos
            : (_probeTeam == MatchTeam.Ct
                ? MatchSceneManagers.PawnSpawn.SandstonePosCt
                : MatchSceneManagers.PawnSpawn.SandstonePosTr);
        _ = Task.Run(async () =>
        {
            Console.WriteLine(
                $"[probe] State loop START id={objectId} pos=({pos.X:0.##},{pos.Y:0.##},{pos.Z:0.##}) " +
                $"interval={FightingStateInterval.TotalMilliseconds:0}ms " +
                "(thin standing pose at the peer's coords — the fat avatar/aim State layout is still " +
                "opaque, so radar tracking may lag; the pawn appears at the peer-anchored spawn)");
            while (!loopCts.Token.IsCancellationRequested
                   && peer.ConnectionState == ConnectionState.Connected
                   && _joinSucceeded
                   && _probePawnObjectId == objectId)
            {
                try
                {
                    await Task.Delay(FightingStateInterval, loopCts.Token);
                }
                catch (OperationCanceledException)
                {
                    return;
                }

                _probeStateSeq++;
                var tickTime = (Environment.TickCount & int.MaxValue) / 1000f;
                var state = MatchCodec.BuildWorldObjectStateStanding(
                    objectId, _probeStateSeq, tickTime, pos.X, pos.Y, pos.Z);
                peer.Send(state, DeliveryMethod.Unreliable);
            }
        });
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
                $"[match-net] PLAYER WorldObjectState flood: seen={n} suppressed≈{suppressed} " +
                $"(logging first {NoisyLogFirst} + every {NoisyLogEveryK}th — " +
                "each State line shows owner actor/name; type roster|status)");
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
        StopProbeStateLoop();
        _cts.Cancel();
        try { Task.WhenAll(_tasks).Wait(800); } catch { /* ignore */ }
        _manager.Stop();
        _cts.Dispose();
    }
}

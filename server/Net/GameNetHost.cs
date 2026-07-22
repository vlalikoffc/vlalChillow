using System.Collections.Concurrent;
using System.Net;
using LiteNetLib;
using StandChillow.LanServer.Lan;
using StandChillow.LanServer.Net.Lobby;

namespace StandChillow.LanServer.Net;

/// <summary>
/// LiteNetLib lobby listen on the advertised join port (7778).
/// Handles Chillow LanLobby envelope (etz) after CONNECT — join snapshot, chat, leave.
/// Match start emits op7/op9 and relies on <see cref="GameMatchHost"/> on UDP 7777.
/// </summary>
public sealed class GameNetHost : IDisposable
{
    public const int DefaultGamePort = LanDiscoveryHost.DefaultGamePort;

    private readonly NetManager _manager;
    private readonly EventBasedNetListener _listener;
    private readonly CancellationTokenSource _cts = new();
    private readonly List<Task> _tasks = new();
    private readonly LobbySession _session;
    private readonly object _gate = new();
    private readonly ConcurrentQueue<Action> _deferred = new();
    private readonly string _captureDir;
    private readonly IPAddress _advertiseLanIp;
    private readonly GameMatchHost _match;
    private long _connectRequests;
    private long _unconnected;
    private int _captureIndex;
    private bool _matchStarted;

    private readonly Action? _onRosterChanged;
    private readonly Action? _onMatchStarted;

    public GameNetHost(
        string lobbyName = "влал хостит рялна",
        int port = DefaultGamePort,
        IReadOnlyList<LanEndpoint>? lanEndpoints = null,
        Action? onRosterChanged = null,
        IPAddress? advertiseLanIp = null,
        Action? onMatchStarted = null)
    {
        _session = new LobbySession(lobbyName);
        _onRosterChanged = onRosterChanged;
        _onMatchStarted = onMatchStarted;
        _captureDir = Path.Combine(AppContext.BaseDirectory, "captures");
        Directory.CreateDirectory(_captureDir);

        _advertiseLanIp = advertiseLanIp
            ?? LanInterfacePicker.PickLanBindAddress()
            ?? lanEndpoints?.FirstOrDefault()?.Address
            ?? IPAddress.Loopback;

        // SINGLETON match listen: construct+bind UDP 7777 exactly once for this lobby process.
        // Never new GameMatchHost / never re-_manager.Start(7777) on play/start or late join.
        _match = new GameMatchHost(_session.LobbyId, GameMatchHost.DefaultMatchPort, _advertiseLanIp);

        _listener = new EventBasedNetListener();
        _manager = new NetManager(_listener)
        {
            AutoRecycle = true,
            BroadcastReceiveEnabled = true,
            IPv6Enabled = false,
            UnconnectedMessagesEnabled = true
        };

        _listener.ConnectionRequestEvent += request =>
        {
            Interlocked.Increment(ref _connectRequests);
            var peek = request.Data.AvailableBytes;
            Console.WriteLine($"[net] CONNECT request from {request.RemoteEndPoint} dataLen={peek} (total={ConnectRequests})");
            // Live phone: dataLen=17 = LiteNetLib Put("MyVerySecretKey") (fwv.cwpw).
            // Real LobbyHost Accept() ignores key; match host uses AcceptIfKey (see GameMatchHost).
            request.Accept();
        };
        _listener.PeerConnectedEvent += peer =>
        {
            Console.WriteLine($"[net] peer CONNECTED {peer.Address}:{peer.Port}");
            _session.OnPeerConnected(peer);
        };
        _listener.PeerDisconnectedEvent += (peer, info) =>
        {
            Console.WriteLine($"[net] peer DISCONNECTED {peer.Address}:{peer.Port}: {info.Reason}");
            var left = _session.OnPeerDisconnected(peer, out var name);
            if (name is not null && left is not null)
            {
                Broadcast(LobbyCodec.BuildMemberLeft(left.ServerMemberId));
                BroadcastServerChat($"{name} Отключился!");
                _onRosterChanged?.Invoke();
            }
        };
        _listener.NetworkReceiveUnconnectedEvent += (ep, reader, type) =>
        {
            Interlocked.Increment(ref _unconnected);
            var n = Math.Min(reader.AvailableBytes, 32);
            var hex = Convert.ToHexString(reader.GetRemainingBytes().AsSpan(0, n));
            Console.WriteLine($"[net] unconnected {type} from {ep} hex={hex}");
        };
        _listener.NetworkReceiveEvent += OnNetworkReceive;
        _listener.NetworkErrorEvent += (ep, error) =>
            Console.WriteLine($"[net] error {ep}: {error}");

        if (!_manager.Start(port))
            throw new InvalidOperationException($"Failed to bind LiteNetLib on UDP {port}");
        Console.WriteLine($"[net] LiteNetLib listen *:{port}");
        Console.WriteLine(
            $"[lobby] title='{_session.LobbyName}' lobbyId={_session.LobbyId} " +
            $"host-player='{LobbySession.ServerPlayerName}' mode={_session.GameModeId} " +
            $"levels=[{string.Join(", ", _session.SelectedLevels)}] auth={LobbyAuth.AuthHash[..8]}…");
        Console.WriteLine(
            $"[lobby] op9 will advertise {_advertiseLanIp}:{GameMatchHost.DefaultMatchPort} (LAN NIC, not VPN)");
        if (lanEndpoints is { Count: > 0 })
            Console.WriteLine($"[net] LAN join targets: {string.Join(", ", lanEndpoints.Select(e => $"{e.Address}:{port}"))}");

        _tasks.Add(Task.Run(PollLoop));
    }

    public long ConnectRequests => Interlocked.Read(ref _connectRequests);
    public long UnconnectedMessages => Interlocked.Read(ref _unconnected);
    public int PeerCount => _manager.ConnectedPeersCount;
    public int JoinedCount => _session.JoinedCount;
    public int Port => _manager.LocalPort;
    public LobbySession Session => _session;
    public GameMatchHost Match => _match;
    public IPAddress AdvertiseLanIp => _advertiseLanIp;
    public bool MatchStarted => _matchStarted;

    public string LobbyName
    {
        get => _session.LobbyName;
        set
        {
            _session.LobbyName = value;
            Console.WriteLine($"[lobby] title → {value}");
        }
    }

    /// <summary>
    /// First Play: emit op7 + SearchingStarted + op9 to existing singleton 7777 (never rebind).
    /// Already in progress: do <b>not</b> present as a new match — only re-advertise the same
    /// in-progress signals (SearchingStarted + op9 → same IP:7777) and refresh discovery.
    /// </summary>
    public bool TryStartMatch(string reason = "manual")
    {
        bool firstStart;
        lock (_gate)
        {
            firstStart = !_matchStarted;
            if (!firstStart)
            {
                Console.WriteLine(
                    $"[lobby] already in progress — not starting a new match ({reason}); " +
                    $"re-advertise SearchingStarted+op9 → {_advertiseLanIp}:{GameMatchHost.DefaultMatchPort} " +
                    $"(singleton listening={_match.IsListening} port={_match.Port} peers={_match.PeerCount})");
            }
            else
                _matchStarted = true;
        }

        if (!_match.IsListening)
        {
            Console.WriteLine(
                "[lobby] FATAL: match LiteNetLib not listening — refusing Play " +
                "(would be Address already in use if we tried a second bind)");
            return false;
        }

        // Bookkeeping only — EnsureLanRoom never binds UDP.
        _match.EnsureLanRoom();
        _session.Searching = true;
        _session.GameInProgress = true;

        PushInProgressLobbySignals(broadcastAll: true);

        if (firstStart)
        {
            Console.WriteLine(
                $"[lobby] MATCH IN PROGRESS ({reason}): mode={_session.GameModeId} " +
                $"levels=[{string.Join(", ", _session.SelectedLevels)}] → " +
                $"op9 {_advertiseLanIp}:{GameMatchHost.DefaultMatchPort} " +
                "(discovery flips to Join / no-map extras)");
            BroadcastServerChat(
                $"матч идёт: {_session.GameModeId} / {string.Join(", ", _session.SelectedLevels)} → {_advertiseLanIp}:{GameMatchHost.DefaultMatchPort}");
        }
        else
        {
            BroadcastServerChat(
                $"матч уже идёт (не новый) → {_advertiseLanIp}:{GameMatchHost.DefaultMatchPort}");
        }

        try { _onMatchStarted?.Invoke(); }
        catch (Exception ex) { Console.WriteLine($"[lobby] onMatchStarted: {ex.Message}"); }

        return firstStart;
    }

    /// <summary>
    /// Lobby signals for game-in-progress (phone host after Play): mode/levels, SearchingStarted,
    /// op9 → existing 7777. Never touches match bind.
    /// </summary>
    private void PushInProgressLobbySignals(bool broadcastAll, NetPeer? onlyPeer = null)
    {
        var modePkt = LobbyCodec.BuildCustomProperties(_session.BuildModeLevelProps());
        var searchingPkt = LobbyCodec.BuildCustomProperty(
            LobbyPropKeys.SearchingStarted, LobbyVariant.FromBool(true));
        var hostingPkt = LobbyCodec.BuildGameHostingState(
            _advertiseLanIp.ToString(), (ushort)GameMatchHost.DefaultMatchPort);

        if (onlyPeer is not null)
        {
            Send(onlyPeer, modePkt);
            Send(onlyPeer, searchingPkt);
            Send(onlyPeer, hostingPkt);
            return;
        }

        if (broadcastAll)
        {
            Broadcast(modePkt);
            Broadcast(searchingPkt);
            Broadcast(hostingPkt);
        }
    }

    private async Task PollLoop()
    {
        while (!_cts.IsCancellationRequested)
        {
            _manager.PollEvents();
            while (_deferred.TryDequeue(out var action))
            {
                try { action(); }
                catch (Exception ex) { Console.WriteLine($"[lobby] deferred error: {ex.Message}"); }
            }
            try { await Task.Delay(15, _cts.Token); }
            catch (OperationCanceledException) { break; }
        }
    }

    private void OnNetworkReceive(NetPeer peer, NetPacketReader reader, byte channel, DeliveryMethod method)
    {
        var len = reader.AvailableBytes;
        var payload = reader.GetRemainingBytes();
        try
        {
            HandleLobbyPayload(peer, payload);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[lobby] parse/handle error from {peer} len={len}: {ex.Message}");
            DumpCapture("err", payload);
        }
    }

    private void HandleLobbyPayload(NetPeer peer, byte[] payload)
    {
        if (payload.Length == 0) return;

        var r = new LobbyReader(payload);
        var opcode = (LobbyOpcode)r.ReadByte();
        Console.WriteLine($"[lobby] from {peer.Address}:{peer.Port} op={opcode} ({(byte)opcode}) len={payload.Length}");

        switch (opcode)
        {
            case LobbyOpcode.OpJoinLobbyRequest:
                HandleJoin(peer, r, payload);
                break;
            case LobbyOpcode.OpSendChatMessageRequest:
                HandleChat(peer, r);
                break;
            case LobbyOpcode.OpChangeSelfPropertyRequest:
                Console.WriteLine($"[lobby] ignore ChangeSelfProperty from {peer.Address} (not implemented)");
                break;
            default:
                Console.WriteLine($"[lobby] unhandled opcode {opcode} — dumping");
                DumpCapture($"op{(byte)opcode}", payload);
                break;
        }
    }

    private void HandleJoin(NetPeer peer, LobbyReader r, byte[] raw)
    {
        DumpCapture("join_req", raw);
        var head = Math.Min(raw.Length, 96);
        Console.WriteLine($"[lobby] join_req head hex={Convert.ToHexString(raw.AsSpan(0, head))}");

        JoinLobbyRequest req;
        try
        {
            req = LobbyCodec.ParseJoinRequest(r);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[lobby] bad JoinLobbyRequest (len={raw.Length} rem≈{r.Remaining}): {ex.Message}");
            Send(peer, LobbyCodec.BuildJoinFail(JoinFailReason.ProfileValidationFailed));
            return;
        }

        if (r.Remaining != 0)
            Console.WriteLine($"[lobby] warning: {r.Remaining} trailing bytes after JoinLobbyRequest");

        Console.WriteLine(
            $"[lobby] JoinRequest name='{req.Profile.Name}' avatar={(req.Profile.Avatar?.Length ?? 0)} " +
            $"ver='{req.VersionHash}' auth='{req.AuthHash}' " +
            $"verOk={LobbyAuth.HashEquals(req.VersionHash, LobbyAuth.VersionHash)} " +
            $"authOk={LobbyAuth.HashEquals(req.AuthHash, LobbyAuth.AuthHash)}");

        var result = _session.HandleJoinRequest(
            peer,
            req,
            matchHostingIp: _matchStarted ? _advertiseLanIp.ToString() : null,
            matchHostingPort: _matchStarted ? (ushort)GameMatchHost.DefaultMatchPort : (ushort)0);
        if (!result.Success)
        {
            Console.WriteLine($"[lobby] join REJECT {result.FailReason}: {result.Detail}");
            if (result.ResponseBytes is not null)
                Send(peer, result.ResponseBytes);
            return;
        }

        Console.WriteLine(
            $"[lobby] join OK → illusion Server+{result.Player!.Name} (serverId={result.Player.ServerMemberId})" +
            (_matchStarted
                ? $" mid-match hasHosting={_advertiseLanIp}:{GameMatchHost.DefaultMatchPort}"
                : " pre-match hasHosting=0"));
        Send(peer, result.ResponseBytes!);

        // JoinResponse stays Server+self (4-cap bypass). Announce real peers via NewMember so
        // OpChatMessageEvent can use ServerMemberId (≥2) without colliding with local self=1.
        AnnounceLobbyMember(result.Player!);
        _onRosterChanged?.Invoke();

        var nick = result.Player.Name;
        Schedule(0, () =>
        {
            SendServerChatTo(peer, LobbySession.WelcomeMessage);
            BroadcastServerChat($"{nick} Подключился!");
        });
        Schedule(50, () => SendServerChatTo(peer, _session.BuildPlayerListMessage()));
        Schedule(80, () => SendServerChatTo(peer,
            _matchStarted
                ? "матч уже идёт — JoinRoom Dedik на 7777 (не новый start)"
                : "чат: /mode /map · /play|/start — начать матч"));

        // Late joiner into running match: also push PropertyChanged + op9 (JoinResponse may already
        // embed hasHosting; op9 is the live phone path clients always listen for).
        if (_matchStarted)
        {
            Schedule(100, () =>
            {
                Console.WriteLine(
                    $"[lobby] late-join → push in-progress signals to {peer.Address} " +
                    $"(existing Dedik room on singleton {_match.Port})");
                PushInProgressLobbySignals(broadcastAll: false, onlyPeer: peer);
            });
        }
    }

    private void HandleChat(NetPeer peer, LobbyReader r)
    {
        string message;
        try { message = LobbyCodec.ParseChatRequest(r); }
        catch (Exception ex)
        {
            Console.WriteLine($"[lobby] bad chat: {ex.Message}");
            return;
        }

        var player = _session.TryGet(peer);
        if (player is not { Joined: true })
        {
            Console.WriteLine($"[lobby] chat from non-joined peer {peer.Address}");
            return;
        }

        message = message.Trim();
        if (message.Length == 0) return;
        Console.WriteLine($"[chat] {player.Name}: {message}");

        // Relay as the player (OpChatMessageEvent senderMemberId = ServerMemberId).
        // Skip sender — client already shows what they typed (no Server echo duplicate).
        RelayPlayerChat(player, message);

        if (TryHandleSlashCommand(player, message))
            return;

        if (IsStartCommand(message))
            TryStartMatch($"chat:{player.Name}");
    }

    /// <summary>
    /// OpChatMessageEvent with the speaker's lobby member id (not Server).
    /// Live phone host path: etq = i32 senderMemberId + string message.
    /// </summary>
    private void RelayPlayerChat(ConnectedPlayer speaker, string message)
    {
        var bytes = LobbyCodec.BuildChatEvent(speaker.ServerMemberId, message);
        foreach (var p in _session.JoinedPlayers())
        {
            if (ReferenceEquals(p.Peer, speaker.Peer)) continue;
            Send(p.Peer, bytes);
        }
    }

    /// <summary>
    /// Introduce <paramref name="joined"/> to existing peers and existing peers to them
    /// (OpLobbyNewMemberEvent). JoinResponse remains Server+self illusion.
    /// </summary>
    private void AnnounceLobbyMember(ConnectedPlayer joined)
    {
        var joinedEvt = LobbyCodec.BuildNewMember(ToLobbyMember(joined));
        foreach (var other in _session.JoinedPlayers())
        {
            if (ReferenceEquals(other.Peer, joined.Peer)) continue;
            Send(other.Peer, joinedEvt);
            Send(joined.Peer, LobbyCodec.BuildNewMember(ToLobbyMember(other)));
        }
    }

    private static LobbyMember ToLobbyMember(ConnectedPlayer p) => new()
    {
        Id = p.ServerMemberId,
        Name = p.Name,
        Avatar = p.Avatar,
    };

    private bool TryHandleSlashCommand(ConnectedPlayer player, string message)
    {
        if (!message.StartsWith('/'))
            return false;

        var body = message[1..].Trim();
        var space = body.IndexOf(' ');
        var cmd = space < 0 ? body : body[..space];
        var args = space < 0 ? "" : body[(space + 1)..].Trim();

        if (cmd.Equals("mode", StringComparison.OrdinalIgnoreCase))
        {
            HandleModeCommand(player, args);
            return true;
        }

        if (cmd.Equals("map", StringComparison.OrdinalIgnoreCase)
            || cmd.Equals("maps", StringComparison.OrdinalIgnoreCase)
            || cmd.Equals("level", StringComparison.OrdinalIgnoreCase)
            || cmd.Equals("levels", StringComparison.OrdinalIgnoreCase))
        {
            HandleMapCommand(player, args);
            return true;
        }

        // /play /start handled by IsStartCommand after this returns false for unknown,
        // but those are also slash-prefixed — treat as start commands here.
        if (IsStartCommand(message))
        {
            TryStartMatch($"chat:{player.Name}");
            return true;
        }

        return false;
    }

    private void HandleModeCommand(ConnectedPlayer player, string args)
    {
        if (string.IsNullOrWhiteSpace(args)
            || !GameModeCatalog.TryResolveMode(args, out var mode))
        {
            SendServerChatTo(player.Peer, GameModeCatalog.FormatModeHelp());
            if (!string.IsNullOrWhiteSpace(args))
                SendServerChatTo(player.Peer, $"неизвестный режим '{args}'");
            return;
        }

        var levels = _session.SelectedLevels
            .Where(L => mode.Levels.Any(c => c.Equals(L, StringComparison.Ordinal)))
            .ToList();
        if (levels.Count == 0)
            levels.Add(GameModeCatalog.DefaultLevelFor(mode.GameModeId));

        _session.GameModeId = mode.GameModeId;
        _session.SelectedLevels = levels;
        BroadcastModeLevelProps();
        _onRosterChanged?.Invoke();

        var msg =
            $"режим → {mode.GameModeId} ({mode.DisplayName}); карты: {string.Join(", ", levels)}";
        Console.WriteLine($"[lobby] {player.Name}: {msg}");
        BroadcastServerChat(msg);
    }

    private void HandleMapCommand(ConnectedPlayer player, string args)
    {
        var modeId = _session.GameModeId;
        if (!GameModeCatalog.TryGetMode(modeId, out var mode))
        {
            SendServerChatTo(player.Peer, GameModeCatalog.FormatMapHelp(modeId, _session.SelectedLevels));
            return;
        }

        if (string.IsNullOrWhiteSpace(args))
        {
            SendServerChatTo(player.Peer, GameModeCatalog.FormatMapHelp(modeId, _session.SelectedLevels));
            return;
        }

        var tokens = args.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length == 0)
        {
            SendServerChatTo(player.Peer, GameModeCatalog.FormatMapHelp(modeId, _session.SelectedLevels));
            return;
        }

        var resolved = new List<string>();
        foreach (var token in tokens)
        {
            if (!GameModeCatalog.TryResolveLevel(mode, token, out var level, out var err))
            {
                SendServerChatTo(player.Peer, err ?? $"bad map '{token}'");
                SendServerChatTo(player.Peer, GameModeCatalog.FormatMapHelp(modeId, _session.SelectedLevels));
                return;
            }

            if (!resolved.Contains(level, StringComparer.Ordinal))
                resolved.Add(level);
        }

        _session.SelectedLevels = resolved;
        BroadcastModeLevelProps();
        _onRosterChanged?.Invoke();

        var msg = $"карты ({mode.GameModeId}) → {string.Join(", ", resolved)}";
        Console.WriteLine($"[lobby] {player.Name}: {msg}");
        BroadcastServerChat(msg);
    }

    /// <summary>op7 CustomProperties — same bag as live mode/level change (GameModeId + SelectedLevels).</summary>
    private void BroadcastModeLevelProps()
    {
        var pkt = LobbyCodec.BuildCustomProperties(_session.BuildModeLevelProps());
        Broadcast(pkt);
        Console.WriteLine(
            $"[lobby] op7 mode/levels → {_session.GameModeId} / [{string.Join(", ", _session.SelectedLevels)}]");
    }

    public static bool IsStartCommand(string message)
    {
        var m = message.Trim();
        if (m.StartsWith('/'))
            m = m[1..];
        return m.Equals("play", StringComparison.OrdinalIgnoreCase)
               || m.Equals("start", StringComparison.OrdinalIgnoreCase)
               || m.Equals("старт", StringComparison.OrdinalIgnoreCase)
               || m.Equals("игра", StringComparison.OrdinalIgnoreCase);
    }

    private void Broadcast(byte[] bytes)
    {
        foreach (var p in _session.JoinedPlayers())
            Send(p.Peer, bytes);
    }

    private void BroadcastServerChat(string text, NetPeer? except = null)
    {
        var bytes = LobbyCodec.BuildChatEvent(LobbySession.ServerMemberId, text);
        foreach (var p in _session.JoinedPlayers())
        {
            if (except is not null && ReferenceEquals(p.Peer, except)) continue;
            Send(p.Peer, bytes);
        }
    }

    private void SendServerChatTo(NetPeer peer, string text) =>
        Send(peer, LobbyCodec.BuildChatEvent(LobbySession.ServerMemberId, text));

    private void Schedule(int delayMs, Action action)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                if (delayMs > 0)
                    await Task.Delay(delayMs, _cts.Token);
                _deferred.Enqueue(action);
            }
            catch (OperationCanceledException) { /* shutting down */ }
        });
    }

    private void Send(NetPeer peer, byte[] payload)
    {
        // Lobby ops use gcv.Reliable. ReliableSequenced cannot fragment (>~1020B throws);
        // real phone JoinResponse is ~13KB with avatars → ReliableOrdered (same as match TX).
        const DeliveryMethod delivery = DeliveryMethod.ReliableOrdered;
        peer.Send(payload, delivery);
        Console.WriteLine(
            $"[lobby] TX → {peer.Address}:{peer.Port} delivery={delivery} len={payload.Length} " +
            $"op={(LobbyOpcode)payload[0]}");
    }

    private void DumpCapture(string tag, byte[] payload)
    {
        try
        {
            var i = Interlocked.Increment(ref _captureIndex);
            var path = Path.Combine(_captureDir, $"{DateTime.UtcNow:yyyyMMdd_HHmmss}_{i:D3}_{tag}_len{payload.Length}.bin");
            File.WriteAllBytes(path, payload);
            Console.WriteLine($"[lobby] captured {path}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[lobby] capture failed: {ex.Message}");
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        try { Task.WhenAll(_tasks).Wait(800); } catch { /* ignore */ }
        _manager.Stop();
        _match.Dispose();
        _cts.Dispose();
    }
}

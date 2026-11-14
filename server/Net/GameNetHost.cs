using System.Collections.Concurrent;
using System.Net;
using LiteNetLib;
using StandChillow.LanServer.Config;
using StandChillow.LanServer.Dashboard;
using StandChillow.LanServer.Diagnostics;
using StandChillow.LanServer.Lan;
using StandChillow.LanServer.Net.Lobby;
using StandChillow.LanServer.Net.Match;

namespace StandChillow.LanServer.Net;

/// <summary>
/// LiteNetLib lobby listen on the advertised join port (7778).
/// Handles Chillow LanLobby envelope (etz) after CONNECT — join snapshot, chat, leave.
/// Match start emits op7/op9 and relies on <see cref="GameMatchHost"/> on UDP 7777.
/// </summary>
public sealed partial class GameNetHost : IDisposable
{
    public const int DefaultGamePort = LanDiscoveryHost.DefaultGamePort;

    private readonly NetManager _manager;
    private readonly EventBasedNetListener _listener;
    private readonly CancellationTokenSource _cts = new();
    private readonly List<Task> _tasks = new();
    private readonly LobbySession _session;
    private readonly object _gate = new();
    private readonly ConcurrentQueue<Action> _deferred = new();
    private readonly IPAddress _advertiseLanIp;
    private readonly GameMatchHost _match;
    private long _connectRequests;
    private long _unconnected;
    private bool _matchStarted;
    private int _disposed;

    private readonly Action? _onRosterChanged;
    private readonly Action? _onMatchStarted;

    /// <summary>Конфиг выделенного сервера. Меняется только через <see cref="ReloadConfig"/>.</summary>
    private ServerConfig _config;

    /// <summary>Кто нажал /ready и какой режим старта активен.</summary>
    private readonly MatchStartGate _startGate = new();

    /// <summary>Как часто опрашивать гейт старта (автостарт / готовность).</summary>
    private static readonly TimeSpan StartGateInterval = TimeSpan.FromSeconds(1);
    private DateTime _lastStartGateCheckUtc = DateTime.MinValue;

    public GameNetHost(
        string lobbyName = "влал хостит рялна",
        int port = DefaultGamePort,
        IReadOnlyList<LanEndpoint>? lanEndpoints = null,
        Action? onRosterChanged = null,
        IPAddress? advertiseLanIp = null,
        Action? onMatchStarted = null,
        ServerConfig? config = null)
    {
        _config = config ?? new ServerConfig();
        ApplyRuntimeConfig(_config);
        _session = new LobbySession(lobbyName);
        _onRosterChanged = onRosterChanged;
        _onMatchStarted = onMatchStarted;

        _advertiseLanIp = advertiseLanIp
            ?? LanInterfacePicker.PickLanBindAddress()
            ?? lanEndpoints?.FirstOrDefault()?.Address
            ?? IPAddress.Loopback;

        // SINGLETON match host object: construct once. UDP 7777 may Stop after MatchOver
        // and Start again on /play — never construct a second GameMatchHost.
        _match = new GameMatchHost(_session.LobbyId, GameMatchHost.DefaultMatchPort, _advertiseLanIp);
        // Mid-match phones chat via ChatManager WorldObjectRpc — wire slash cmds (/set start).
        _match.OnMatchChatSlashCommand = HandleMatchChatSlashCommand;
        // Ordinary match chat → console + dashboard (the катка used to be a blind spot).
        _match.OnMatchChatText = (nick, text) =>
            DashboardHub.PostChat(
                string.IsNullOrWhiteSpace(nick) ? "?" : nick, text, fromServer: false);
        // Decisive match reactions → Server OpChat when MatchHostSettings.DebugMatchChat.
        _match.OnServerDebugChat = BroadcastConsoleChat;
        // Wins-needed / series MatchResults + 5s → stop 7777, idle lobby (keep mode/map).
        _match.OnMatchOverReturnToLobby = () =>
            _deferred.Enqueue(() => ReturnPlayersToLobbyIdle("match-over"));
        // Match JoinRoom Found / peer leave → Server chat + per-player lobby presence.
        _match.OnMatchPlayerJoined = name =>
            _deferred.Enqueue(() => NotifyMatchPlayerJoined(name));
        _match.OnMatchPlayerLeft = name =>
            _deferred.Enqueue(() => NotifyMatchPlayerLeft(name));

        // Баны: реестр читается с диска до первого пакета, иначе первый вошедший
        // забаненный успел бы проскочить. Матч получает тот же реестр.
        InitBans();

        // Админские ники требуют код на этот запуск: ник присылает клиент, поэтому одного
        // ника мало. Матч обязан спрашивать то же самое — иначе админский ник без кода
        // зашёл бы прямо на 7777 мимо лобби.
        LogAdminAuthStartup();
        _match.IsUnauthorizedAdmin = IsUnauthorizedAdmin;

        _listener = new EventBasedNetListener();
        _manager = new NetManager(_listener)
        {
            AutoRecycle = true,
            BroadcastReceiveEnabled = true,
            IPv6Enabled = false,
            UnconnectedMessagesEnabled = true,
            // Tolerate a laggy phone without dropping the whole session.
            DisconnectTimeout = MatchTuning.DisconnectTimeoutMs,
            PacketPoolSize = MatchTuning.PacketPoolSize,
            UpdateTime = MatchTuning.NetUpdateTimeMs,
            PingInterval = MatchTuning.PingIntervalMs,
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
            lock (_shadowGate)
                _shadowPeers.Remove(peer);
            ForgetPendingAdmin(peer);
            var left = _session.OnPeerDisconnected(peer, out var name);
            if (name is not null && left is not null)
            {
                // Full roster: OpLobbyMemberLeftEvent so remaining clients drop the member.
                // Match leave chat is separate (OnMatchPlayerLeft); lobby disconnect is silent
                // unless they never entered катка (optional notice only when still Lobby).
                var leftId = left.ServerMemberId;
                var wasInMatch = left.Status == PlayerLobbyStatus.InMatch;
                Broadcast(LobbyCodec.BuildMemberLeft(leftId));

                // Отключившийся не должен вечно блокировать старт по готовности.
                if (_startGate.Remove(name))
                {
                    Console.WriteLine($"[start-gate] {name}: голос снят (отключился)");
                    AnnounceReadyProgress();
                }

                if (!wasInMatch)
                    BroadcastServerChat($"{name} Отключился!");
                BroadcastServerChat(_session.BuildPlayerListMessage());
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

        if (!_manager.Start(NetBind.Address, IPAddress.IPv6Any, port))
            throw new InvalidOperationException($"Failed to bind LiteNetLib on UDP {port}");
        Console.WriteLine($"[net] LiteNetLib listen {NetBind.Describe}:{port}");
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

    /// <summary>
    /// Optional plugin slash handler: (speaker, commandWithoutSlash, args) → handled.
    /// Called only after built-in <c>/mode</c>/<c>/map</c>/<c>/set</c>/<c>/play</c>/<c>/start</c>.
    /// </summary>
    public Func<string, string, string, bool>? PluginChatCommandHandler { get; set; }

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
    /// First Play: emit op7 + SearchingStarted + op9 to existing match host (rebind 7777 if
    /// suspended after MatchOver). Rematch (<c>/play</c> after clients returned to lobby):
    /// <b>tear down</b> previous Dedik room (MatchOver / mid-round / stale actors) then start
    /// fresh — do not re-advertise into a dead match (loading hang). Never call this for
    /// <c>/set start</c> / console <c>start</c> while a match is already live — those arm
    /// WarmUp via <see cref="ArmMatchStart"/>.
    /// </summary>
    public bool TryStartMatch(string reason = "manual")
    {
        bool rematch;
        lock (_gate)
        {
            rematch = _matchStarted || _match.HasStaleMatchState();
            _matchStarted = true;
        }

        if (!_match.EnsureMatchListening())
        {
            Console.WriteLine(
                "[lobby] FATAL: match LiteNetLib not listening — refusing Play " +
                "(rebind UDP 7777 failed after MatchOver suspend)");
            lock (_gate)
                _matchStarted = false;
            return false;
        }

        if (rematch)
        {
            Console.WriteLine(
                $"[lobby] rematch path ({reason}): HardReset before new Play " +
                "(explicit /play only — not /set start)");
            _match.HardResetMatchForNewStart(reason);
        }

        // Bookkeeping only — EnsureLanRoom never binds UDP.
        // Gap C0/C1 must match lobby before phone JoinRoom Found (else always Ranked2v2/Sandstone).
        SyncMatchSelectionFromLobby();
        _match.EnsureLanRoom();
        _session.Searching = true;
        _session.GameInProgress = true;

        PushInProgressLobbySignals(broadcastAll: true);

        Console.WriteLine(
            $"[lobby] MATCH IN PROGRESS ({reason}{(rematch ? ", rematch" : "")}): " +
            $"mode={_session.GameModeId} " +
            $"levels=[{string.Join(", ", _session.SelectedLevels)}] → " +
            $"op9 {_advertiseLanIp}:{GameMatchHost.DefaultMatchPort} " +
            "(discovery flips to Join / no-map extras)");
        BroadcastServerChat(
            $"матч идёт: {_session.GameModeId} / {string.Join(", ", _session.SelectedLevels)} → {_advertiseLanIp}:{GameMatchHost.DefaultMatchPort}");

        try { _onMatchStarted?.Invoke(); }
        catch (Exception ex) { Console.WriteLine($"[lobby] onMatchStarted: {ex.Message}"); }

        return true;
    }

    /// <summary>
    /// After MatchResults + 5s: stop match listen (7777), clear Dedik, lobby idle with
    /// mode/map preserved. Next <c>/play</c> + <c>/set start</c> launches cleanly.
    /// </summary>
    public void ReturnPlayersToLobbyIdle(string reason)
    {
        lock (_gate)
            _matchStarted = false;

        // Preserve GameModeId + SelectedLevels — only clear in-progress flags.
        _session.Searching = false;
        _session.GameInProgress = false;
        _session.ResetAllToLobby();
        MatchHostSettings.MatchStartArmed = false;

        // Катка закончилась — голоса /ready прошлого матча не должны тянуться в следующий.
        _startGate.Clear();

        _match.SuspendMatchListen(reason);
        PushIdleLobbySignals(broadcastAll: true);

        Console.WriteLine(
            $"[lobby] RETURN TO LOBBY ({reason}): mode={_session.GameModeId} " +
            $"levels=[{string.Join(", ", _session.SelectedLevels)}] " +
            "SearchingStarted=false hasHosting=0 match UDP stopped");
        BroadcastServerChat(
            $"матч окончен — лобби (режим {_session.GameModeId} / карта сохранена). /play затем /set start");

        try { _onRosterChanged?.Invoke(); }
        catch (Exception ex) { Console.WriteLine($"[lobby] onRosterChanged after return: {ex.Message}"); }
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

    /// <summary>
    /// Pre-match / post-MatchOver idle: keep mode+map, SearchingStarted=false, hasHosting=0.
    /// </summary>
    private void PushIdleLobbySignals(bool broadcastAll)
    {
        var modePkt = LobbyCodec.BuildCustomProperties(_session.BuildModeLevelProps());
        var searchingPkt = LobbyCodec.BuildCustomProperty(
            LobbyPropKeys.SearchingStarted, LobbyVariant.FromBool(false));
        var hostingPkt = LobbyCodec.BuildGameHostingState(null, 0);

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

            // Автостарт / старт по готовности. Держится на этом же потоке, потому что
            // ArmMatchStart шлёт лобби-пакеты, а слать их лучше из poll-потока.
            var now = DateTime.UtcNow;
            if (now - _lastStartGateCheckUtc >= StartGateInterval)
            {
                _lastStartGateCheckUtc = now;
                try { EvaluateMatchStartGate(); }
                catch (Exception ex) { Console.WriteLine($"[start-gate] error: {ex.Message}"); }
            }

            // Баны: раз в admin.ban.sweepSec снять истёкшие (и отпустить теневые сокеты),
            // раз в admin.ban.shadowRefreshSec обновить «Разбан через: …».
            try { SweepBans(); }
            catch (Exception ex) { Console.WriteLine($"[ban] sweep: {ex.Message}"); }
            try { RefreshShadowCountdowns(); }
            catch (Exception ex) { Console.WriteLine($"[ban] countdown: {ex.Message}"); }

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
        if (Diag.LogVerbose)
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

        // Справочник «ник ↔ id ↔ IP»: нужен, чтобы /ban находил человека и после
        // выхода из катки. Пишется на диск только при новом нике/адресе, не на каждый вход.
        _directory.Remember(req.Profile.Name, peer.Address?.ToString(), DateTime.UtcNow);

        // Админский ник без кода — как забаненный: теневой лобби, ни команд, ни матча.
        // Проверяем до банов: админ от бана и так освобождён, а код нужен в любом случае.
        if (IsUnauthorizedAdmin(req.Profile.Name, peer.Address))
        {
            HandleAdminAuthJoin(peer, req);
            return;
        }

        // Бан — до любых side-effects: ни слота в росписи, ни приветствия, ни op9 про
        // идущий матч забаненный не получает. Проверка только здесь, на входе.
        var banDecision = InspectBan(req.Profile.Name, peer.Address);
        if (banDecision.Banned)
        {
            HandleShadowJoin(peer, req, banDecision);
            return;
        }
        if (banDecision.AdminExempt)
            Console.WriteLine($"[ban] '{req.Profile.Name}' — админ или сервер, бан не применяется");

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
            $"[lobby] join OK → {result.Player!.Name} id={result.Player.ServerMemberId}" +
            (_matchStarted
                ? $" mid-match hasHosting={_advertiseLanIp}:{GameMatchHost.DefaultMatchPort}"
                : " pre-match hasHosting=0"));
        Send(peer, result.ResponseBytes!);

        // Full roster: announce joiner to everyone already in lobby (op4 NewMember).
        var self = result.Player!;
        var newMemberPkt = LobbyCodec.BuildNewMember(LobbySession.ToLobbyMember(self));
        foreach (var p in _session.JoinedPlayers())
        {
            if (ReferenceEquals(p.Peer, peer)) continue;
            Send(p.Peer, newMemberPkt);
        }
        _onRosterChanged?.Invoke();

        Schedule(0, () =>
        {
            var welcome = LobbySession.WelcomeMessageOverride ?? LobbySession.WelcomeMessage;
            if (!string.IsNullOrWhiteSpace(welcome))
                SendServerChatTo(peer, welcome);
            // Lobby join is quiet for match-style Подключился — that fires on JoinRoom Found.
            BroadcastServerChat(_session.BuildPlayerListMessage());
        });
        Schedule(80, () => SendServerChatTo(peer,
            _matchStarted
                ? "матч уже идёт — JoinRoom Dedik на 7777 (не новый start)"
                : FormatCommandHelp()));

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

        message = message.Trim();
        if (message.Length == 0) return;

        // Неавторизованный админ: единственное, что он может написать, — код.
        // Всё остальное (в том числе /ban) сюда не проходит.
        if (IsPendingAdmin(peer) && HandlePendingAdminChat(peer, message))
            return;

        var player = _session.TryGet(peer);
        if (player is not { Joined: true })
        {
            // Забаненный в теневом лобби: не отвечаем и не спамим консоль на каждое сообщение.
            if (IsShadowPeer(peer))
                return;
            Console.WriteLine($"[lobby] chat from non-joined peer {peer.Address}");
            return;
        }

        // Slash commands: execute silently — do NOT relay to lobby peers or PostChat the command.
        if (message.StartsWith('/'))
        {
            Console.WriteLine($"[chat] {player.Name}: {message} (command, silent)");
            if (!TryHandleSlashCommand(player, message))
                ReplyPrivate(player, FormatUnknownCommandHelp(message));
            return;
        }

        Console.WriteLine($"[chat] {player.Name}: {message}");
        DashboardHub.PostChat(player.Name, message);

        // Relay with the speaker's real member id — clients now have the full roster.
        RelayPlayerChat(player, message);
    }

    /// <summary>
    /// ChatManager mid-match slash. Match peer ≠ lobby peer (7777 vs 7778), поэтому
    /// игрока ищем по нику актора, а IP оставляем запасным путём.
    /// </summary>
    private void HandleMatchChatSlashCommand(string actorName, IPAddress? matchPeerIp, string message)
    {
        var resolved = ResolveMatchChatPlayer(actorName, matchPeerIp, out var reason);
        if (resolved is null)
        {
            // Не молчим: и в консоль, и в лог с причиной — иначе команда выглядит
            // «понятой, но проигнорированной», что и было в отчёте.
            Console.WriteLine(
                $"[chat] ChatManager slash НЕ выполнен: actor='{actorName}' ip={matchPeerIp} " +
                $"причина={reason} команда='{message}'");

            // Молчать нельзя: игрок в катке видит только матч-чат, и «ничего не произошло»
            // выглядит как проглоченная команда. Отвечаем туда, откуда пришло.
            _match.SendServerChat(
                $"команда не выполнена: не понял, кто ты ({reason})",
                nick: actorName,
                alsoLastChanceIp: matchPeerIp);
            return;
        }

        var (player, resolvedBy) = resolved.Value;
        Console.WriteLine(
            $"[chat] {player.Name}: {message} (ChatManager command, silent; " +
            $"actor='{actorName}' resolvedBy={resolvedBy})");
        if (!TryHandleSlashCommand(player, message))
            ReplyPrivate(player, FormatUnknownCommandHelp(message));
    }

    /// <summary>
    /// Кто именно написал команду в катке (тонкая обёртка над чистым
    /// <see cref="MatchChatAuthorResolver.Resolve"/> — его и покрывают тесты).
    /// </summary>
    private (ConnectedPlayer Player, string ResolvedBy)? ResolveMatchChatPlayer(
        string actorName,
        IPAddress? matchPeerIp,
        out string reason)
    {
        var joined = _session.JoinedPlayers();
        var byIp = matchPeerIp is null
            ? Array.Empty<ConnectedPlayer>()
            : _session.JoinedPlayersByIp(matchPeerIp).ToArray();

        var hit = MatchChatAuthorResolver.Resolve(actorName, joined, byIp, out reason);
        return hit is { } h ? (h.Player, h.ResolvedBy) : null;
    }

    /// <summary>
    /// Relay a player's chat to the OTHER peers. Sender is skipped (their client already echoes).
    /// </summary>
    private void RelayPlayerChat(ConnectedPlayer speaker, string message)
    {
        var bytes = LobbyCodec.BuildChatEvent(speaker.ServerMemberId, message);

        // Эхо автору тоже. Клиент не рисует собственное сообщение сам — ждёт кадр от
        // сервера, поэтому без эха игрок видел пустое окно и решал, что «чат не работает».
        foreach (var p in _session.JoinedPlayers())
            Send(p.Peer, bytes);

        // В катке свой чат (ChatManager): лобби-кадр туда не доходит. Игрок в матче
        // пишет через лобби-сокет, который клиент держит весь матч, — поэтому его текст
        // надо дослать в матч-чат, иначе его не видит никто из катки.
        if (speaker.Status == PlayerLobbyStatus.InMatch && _match.IsListening)
            SendServerChatToMatch($"{speaker.Name}: {message}");
    }

        /// <summary>op7 CustomProperties — same bag as live mode/level change (GameModeId + SelectedLevels).</summary>
    private void BroadcastModeLevelProps()
    {
        SyncMatchSelectionFromLobby();
        var pkt = LobbyCodec.BuildCustomProperties(_session.BuildModeLevelProps());
        Broadcast(pkt);
        Console.WriteLine(
            $"[lobby] op7 mode/levels → {_session.GameModeId} / [{string.Join(", ", _session.SelectedLevels)}]");
    }

    /// <summary>Copy lobby GameModeId + first SelectedLevels entry into match gap C0/C1.</summary>
    private void SyncMatchSelectionFromLobby()
    {
        var level = _session.SelectedLevels.Count > 0
            ? _session.SelectedLevels[0]
            : LobbyPropKeys.DefaultSelectedLevel;
        _match.SetMatchSelection(_session.GameModeId, level);
    }

    /// <summary>
    /// Explicit rematch / first Play: <c>/play</c> (or Russian <c>игра</c>). May HardReset.
    /// </summary>
    public static bool IsPlayCommand(string message)
    {
        var m = message.Trim();
        if (m.StartsWith('/'))
            m = m[1..];
        return m.Equals("play", StringComparison.OrdinalIgnoreCase)
               || m.Equals("игра", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Arm WarmUp gate (<c>/start</c>, <c>старт</c>, <c>startmatch</c>) — never HardReset.
    /// First launch still needs <see cref="IsPlayCommand"/> / <c>play</c> if match not up.
    /// </summary>
    public static bool IsArmWarmupCommand(string message)
    {
        var m = message.Trim();
        if (m.StartsWith('/'))
            m = m[1..];
        return m.Equals("start", StringComparison.OrdinalIgnoreCase)
               || m.Equals("startmatch", StringComparison.OrdinalIgnoreCase)
               || m.Equals("старт", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Legacy alias — prefer <see cref="IsPlayCommand"/> / <see cref="IsArmWarmupCommand"/>.</summary>
    public static bool IsStartCommand(string message) =>
        IsPlayCommand(message) || IsArmWarmupCommand(message);

    /// <summary>
    /// Console/chat <c>start</c>: if match not launched yet, first Play (no rematch teardown);
    /// if already in Dedik WaitingPlayers / mid-match, arm WarmUp only (never HardReset).
    /// </summary>
    /// <returns>
    /// true — матч был запущен этим вызовом (первый Play); false — только armed WarmUp.
    /// </returns>
    public bool TryArmWarmupOrFirstPlay(string who)
    {
        bool started;
        lock (_gate)
            started = _matchStarted;

        if (!started)
        {
            Console.WriteLine(
                $"[lobby] {who}: start before Play — launching match (first start, no teardown)");
            TryStartMatch(who);
            return true;
        }

        ArmMatchStart(who);
        return false;
    }

    /// <summary>
    /// Arm WarmUp gate (<see cref="MatchHostSettings.MatchStartArmed"/>). Both teams ≥1
    /// then enter WarmUp on next tick — does not replace lobby <c>/play</c> (op9/match bind).
    /// </summary>
    public void ArmMatchStart(string who)
    {
        MatchHostSettings.MatchStartArmed = true;
        var msg = "матч armed — WarmUp когда обе команды ≥1 (/set start)";
        Console.WriteLine($"[lobby] {who}: {msg} (MatchStartArmed=true)");
        BroadcastServerChat(msg);
        // Nudge flow if already waiting with both teams.
        _match.NudgeMatchStartGate();
    }

    /// <summary>
    /// Режимы <c>auto</c> и <c>ready</c>: решает, пора ли армить старт.
    /// В режиме <c>manual</c> ничего не делает — старт остаётся за командой.
    /// </summary>
    private void EvaluateMatchStartGate()
    {
        if (_startGate.Mode == MatchStartMode.Manual)
            return;

        bool started;
        lock (_gate)
            started = _matchStarted;
        if (!started)
            return;

        if (MatchHostSettings.MatchStartArmed)
            return;

        var inMatch = InMatchPlayerNames();
        _match.TryGetTeamCounts(out var ct, out var tr);

        if (!_startGate.ShouldStart(inMatch, ct, tr, out var reason))
            return;

        Console.WriteLine($"[start-gate] {reason} — стартую (то же, что /set start)");
        ArmMatchStart("start-gate");
    }

    /// <summary>Ники тех, кто сейчас реально в катке (а не просто сидит в лобби).</summary>
    private List<string> InMatchPlayerNames()
    {
        var names = new List<string>();
        foreach (var player in _session.JoinedPlayers())
        {
            if (player.Status == PlayerLobbyStatus.InMatch && !string.IsNullOrWhiteSpace(player.Name))
                names.Add(player.Name);
        }

        return names;
    }

    /// <summary>
    /// Применяет конфиг к живому хосту. Только выделенный сервер:
    /// вызывается из конструктора и из <see cref="ReloadConfig"/>.
    /// </summary>
    private void ApplyRuntimeConfig(ServerConfig config)
    {
        _startGate.Apply(config);
        MatchHostSettings.Apply(config.Timings);
        if (!MatchHostSettings.DebugMatchChatLocked)
            MatchHostSettings.DebugMatchChat = config.Debug.MatchChat;
        MatchTuning.Apply(config.Tuning);
        NetBind.Apply(config.Network);

        // MaxMembers уже прокламлен в 1..255 конфигом — приведение безопасно.
        LobbySession.MaxMembersOverride = (byte)Math.Clamp(config.Server.MaxMembers, 1, 255);
        LobbySession.WelcomeMessageOverride = config.Server.WelcomeMessage;

        // CLI/--verbose уже мог включить трейс — конфиг не должен его выключать.
        Diag.Apply(config.Debug, config.Tuning.HealthIntervalSec, config.Launch.Verbose);

        // --verbose исторически включает captures; debug.captures управляет ими явно.
        CaptureSink.Configure(
            config.Debug.Captures || Diag.CapturesForced,
            config.Debug.CapturesDir is { Length: > 0 } dir
                ? dir
                : Path.Combine(AppContext.BaseDirectory, "captures"),
            config.Debug.CapturesMaxFiles,
            config.Debug.CapturesMaxBytesMb * 1024L * 1024L);
    }

    /// <summary>Показать действующий конфиг (консоль: <c>config</c>).</summary>
    public void ShowConfig(Action<string> feedback)
    {
        var admins = _config.Admin.Nicknames.Count == 0
            ? "(нет)"
            : string.Join(", ", _config.Admin.Nicknames);

        feedback($"config: {ServerConfig.LastLoadedPath}");
        feedback($"  админы: {admins}");
        if (_config.Admin.Nicknames.Count == 0)
            feedback("    (пусто — админ-команд ни у кого нет; добавь \"nicknames\": [ \"ник\" ] " +
                     $"в admin{{}} файла {ServerConfig.LastLoadedPath})");
        feedback($"  код админа на этот запуск: {_adminAuth.Code} (auth reset — новый)");
        feedback($"  только для админов: " +
                 (_config.Admin.AdminOnlyCommands.Count == 0
                     ? "(нет)"
                     : string.Join(", ", _config.Admin.AdminOnlyCommands)));
        feedback($"  катку запускает кто угодно: {(_config.Admin.AnyoneCanStart ? "да" : "нет")}");
        feedback(_config.Issues.Count == 0
            ? "  значения: все корректны"
            : $"  значения: некорректных {_config.Issues.Count} — применены стандартные");
        feedback($"  режим старта: {_config.DescribeStartMode()}");
        feedback($"  тайминги: {MatchHostSettings.FormatStatusLine()}");
        feedback($"  темп: tick={MatchTuning.TickMs}ms state={MatchTuning.StateIntervalMs}ms " +
                 $"queueLimit={MatchTuning.ReliableQueueSoftLimit} pool={MatchTuning.PacketPoolSize}");
        feedback($"  отладка: verbose={(Diag.Verbose ? "да" : "нет")} " +
                 $"captures={(CaptureSink.Enabled ? "да" : "нет")} health={(Diag.StatsEnabled ? "да" : "нет")}");
        feedback($"  сеть: bind={_config.Network.BindAddress} " +
                 $"ignoreVpn={(_config.Network.IgnoreVpn ? "да" : "нет")} " +
                 $"advertiseIp={_config.Network.AdvertiseIp ?? "авто"}");
        feedback($"  лобби: порт={_config.Server.LobbyPort} maxMembers={_config.Server.MaxMembers}");
    }

    /// <summary>Перечитать <c>config.json</c> без перезапуска (консоль: <c>config reload</c>).</summary>
    public void ReloadConfig(Action<string> feedback)
    {
        var config = ServerConfig.Load(ServerConfig.LastLoadedPath);
        ApplyConfig(config);
        feedback($"config перечитан: {ServerConfig.LastLoadedPath}");
        ShowConfig(feedback);
    }

    /// <summary>
    /// Применяет уже разобранный конфиг к живому хосту — общий путь для старта,
    /// ручного <c>config reload</c> и автоматического перечитая при изменении файла.
    /// </summary>
    /// <remarks>
    /// Перенос<i>имени</i> лобби здесь не применяется: оно уже уехало в discovery-пакеты,
    /// и менять его на лету — отдельная задача (см. <c>/title</c> в дашборде).
    /// Порт лобби и параметры LiteNetLib тоже требуют перезапуска — об этом сообщает
    /// <see cref="ServerConfig.LogIssues"/>.
    /// </remarks>
    public void ApplyConfig(ServerConfig config)
    {
        _config = config;
        ApplyRuntimeConfig(_config);

        if (_bans is not null)
        {
            _bans.LearnAliases = _config.Admin.Ban.BindAliases;
            _match.ExposeServerUid = _config.Admin.Ban.ExposeIds;
            var wanted = ResolveBanPath(_config.Admin.Ban.File);
            if (_banStore is not null
                && !string.Equals(wanted, _banStore.Path, StringComparison.Ordinal))
            {
                Console.WriteLine(
                    $"[ban] новый файл банов ({wanted}) применится после перезапуска " +
                    $"(сейчас {_banStore.Path})");
            }
        }

        // Голоса /ready имеют смысл только в режиме ready — иначе это мусор.
        if (_config.StartMode != MatchStartMode.Ready)
            _startGate.Clear();
    }

    /// <summary>
    /// Console/dashboard free-text → lobby chat as Server nick to all joined peers.
    /// Mid-match phones use ChatManager WorldObjectRpc; lobby OpChat still reaches both sides
    /// (same path as phone lobby chat). Prefer this over inventing ChatManager payloads.
    /// </summary>
    public void BroadcastConsoleChat(string text)
    {
        text = text.Trim();
        if (text.Length == 0) return;
        Console.WriteLine($"[chat] {LobbySession.ServerPlayerName}: {text}");
        BroadcastServerChat(text);
    }

    private void Broadcast(byte[] bytes)
    {
        foreach (var p in _session.JoinedPlayers())
            Send(p.Peer, bytes);
    }

    private void BroadcastServerChat(string text, NetPeer? except = null)
    {
        DashboardHub.PostChat(LobbySession.ServerPlayerName, text, fromServer: true);
        var bytes = LobbyCodec.BuildChatEvent(LobbySession.ServerMemberId, text);
        foreach (var p in _session.JoinedPlayers())
        {
            if (except is not null && ReferenceEquals(p.Peer, except)) continue;
            Send(p.Peer, bytes);
        }

        // Лобби-кадр (OpChat) не доходит до игрока в катке — там другая сессия на 7777
        // и другой чат (ChatManager). Без этой строки сервер «говорил в пустоту»:
        // в лобби сообщение видно, в матче — нет.
        SendServerChatToMatch(text);
    }

    /// <summary>
    /// Продублировать серверное сообщение в матч настоящим ChatManager-пакетом
    /// (та же форма, что телефоны шлют друг другу — см. <c>BuildChatManagerText</c>).
    /// </summary>
    private void SendServerChatToMatch(string text)
    {
        try
        {
            var peers = _match.SendServerChat(text);
            if (peers > 0)
            {
                Console.WriteLine($"[chat] Server → match: {text} (peers={peers})");
                return;
            }

            // Широкая рассылка без адресата раньше терялась молча: в катке ещё нет пиров
            // (сообщение «матч armed» уходит ровно в этот момент) или их сцена строится за
            // world-gate. Адресные ответы уже умеют ждать в очереди — пусть умеет и эта.
            // Но только когда в катке кто-то есть: до старта туда зеркалится каждый лобби-ростер,
            // и копить их — значит высыпать в чат восемь устаревших строк при первом же джойне.
            if (_match.IsListening && _match.HasMatchPeers)
                RememberMatchReply(BroadcastReplyNick, text, address: null);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[chat] Server → match failed: {ex.Message}");
        }
    }

    /// <summary>Лобби-сообщение конкретному сокету (приветствие при входе, служебное).</summary>
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
        try
        {
            peer.Send(payload, delivery);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[lobby] send failed to {peer.Address}: {ex.Message}");
            return;
        }

        // Per-packet lobby TX logging is a hot path (every chat/roster fan-out). Keep it
        // behind the verbose switch; captures likewise.
        if (Diag.LogVerbose)
        {
            Console.WriteLine(
                $"[lobby] TX → {peer.Address}:{peer.Port} delivery={delivery} len={payload.Length} " +
                $"op={(LobbyOpcode)payload[0]}");
            DumpCapture($"lobby_tx_{(LobbyOpcode)payload[0]}", payload);
        }
    }

    private void DumpCapture(string tag, byte[] payload) => CaptureSink.Capture(tag, payload);

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        try { _cts.Cancel(); } catch { /* ignore */ }
        try { Task.WhenAll(_tasks).Wait(800); } catch { /* ignore */ }
        // Disconnect peers + PollEvents so LiteNetLib sends close packets before Stop.
        LiteNetGracefulStop.DisconnectFlushAndStop(_manager);
        _match.Dispose();
        RemoveAuthCodeFile();
        try { _cts.Dispose(); } catch { /* ignore */ }
    }
}

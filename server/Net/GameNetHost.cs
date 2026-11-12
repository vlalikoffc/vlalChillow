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

    /// <summary>Конфиг сервера (админы + режим старта). Меняется только через <see cref="ReloadConfig"/>.</summary>
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
        _startGate.Apply(_config);
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

        _listener = new EventBasedNetListener();
        _manager = new NetManager(_listener)
        {
            AutoRecycle = true,
            BroadcastReceiveEnabled = true,
            IPv6Enabled = false,
            UnconnectedMessagesEnabled = true,
            // Tolerate a laggy phone without dropping the whole session.
            DisconnectTimeout = 10_000,
            PacketPoolSize = 4096,
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
        if (Diagnostics.LogVerbose)
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
            SendServerChatTo(peer, LobbySession.WelcomeMessage);
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

        var player = _session.TryGet(peer);
        if (player is not { Joined: true })
        {
            Console.WriteLine($"[lobby] chat from non-joined peer {peer.Address}");
            return;
        }

        message = message.Trim();
        if (message.Length == 0) return;

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
        var player = ResolveMatchChatPlayer(actorName, matchPeerIp);
        if (player is null)
        {
            // Раньше тут был молчаливый return — команда просто исчезала, и было
            // не понять, дошла ли она вообще. Теперь причина видна в консоли.
            Console.WriteLine(
                $"[chat] ChatManager slash: не удалось определить игрока " +
                $"(actor='{actorName}' ip={matchPeerIp}) — команда не выполнена: {message}");
            return;
        }

        Console.WriteLine($"[chat] {player.Name}: {message} (ChatManager command, silent)");
        if (!TryHandleSlashCommand(player, message))
            ReplyPrivate(player, FormatUnknownCommandHelp(message));
    }

    /// <summary>
    /// Кто именно написал команду в катке. Порядок: ник актора → IP → единственный игрок.
    /// </summary>
    private ConnectedPlayer? ResolveMatchChatPlayer(string actorName, IPAddress? matchPeerIp)
    {
        var joined = _session.JoinedPlayers();
        if (joined.Count == 0)
            return null;

        if (!string.IsNullOrWhiteSpace(actorName))
        {
            var nick = actorName.Trim();
            foreach (var candidate in joined)
            {
                if (string.Equals(candidate.Name, nick, StringComparison.OrdinalIgnoreCase))
                    return candidate;
            }
        }

        if (matchPeerIp is not null)
        {
            var byIp = _session.TryGetByIp(matchPeerIp);
            if (byIp is not null)
                return byIp;
        }

        return joined.Count == 1 ? joined[0] : null;
    }

    /// <summary>
    /// Relay a player's chat to the OTHER peers. Sender is skipped (their client already echoes).
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
    public void TryArmWarmupOrFirstPlay(string who)
    {
        bool started;
        lock (_gate)
            started = _matchStarted;

        if (!started)
        {
            Console.WriteLine(
                $"[lobby] {who}: start before Play — launching match (first start, no teardown)");
            TryStartMatch(who);
            return;
        }

        ArmMatchStart(who);
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

    /// <summary>Показать действующий конфиг (консоль: <c>config</c>).</summary>
    public void ShowConfig(Action<string> feedback)
    {
        var admins = _config.Admin.Nicknames.Count == 0
            ? "(нет)"
            : string.Join(", ", _config.Admin.Nicknames);

        feedback($"config: {ServerConfig.LastLoadedPath}");
        feedback($"  админы: {admins}");
        feedback($"  только для админов: {string.Join(", ", _config.Admin.AdminOnlyCommands)}");
        feedback($"  /start разрешён всем: {(_config.Admin.AnyoneCanStart ? "да" : "нет")}");
        feedback($"  режим старта: {_config.DescribeStartMode()}");
    }

    /// <summary>Перечитать <c>config.json</c> без перезапуска (консоль: <c>config reload</c>).</summary>
    public void ReloadConfig(Action<string> feedback)
    {
        _config = ServerConfig.Load(ServerConfig.LastLoadedPath);
        _startGate.Apply(_config);

        // Голоса /ready имеют смысл только в режиме ready — иначе это мусор.
        if (_config.StartMode != MatchStartMode.Ready)
            _startGate.Clear();

        feedback($"config перечитан: {ServerConfig.LastLoadedPath}");
        ShowConfig(feedback);
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
        if (Diagnostics.LogVerbose)
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
        try { _cts.Dispose(); } catch { /* ignore */ }
    }
}

using System.Collections.Concurrent;
using System.Net;
using LiteNetLib;
using StandChillow.LanServer.Dashboard;
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
    private readonly string _captureDir;
    private readonly IPAddress _advertiseLanIp;
    private readonly GameMatchHost _match;
    private long _connectRequests;
    private long _unconnected;
    private int _captureIndex;
    private bool _matchStarted;
    private int _disposed;

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
                // Full roster: OpLobbyMemberLeftEvent so remaining clients drop the member.
                // Match leave chat is separate (OnMatchPlayerLeft); lobby disconnect is silent
                // unless they never entered катка (optional notice only when still Lobby).
                var leftId = left.ServerMemberId;
                var wasInMatch = left.Status == PlayerLobbyStatus.InMatch;
                Broadcast(LobbyCodec.BuildMemberLeft(leftId));
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
    /// ChatManager mid-match slash (match peer IP ≠ lobby peer). Resolve lobby player by IP.
    /// </summary>
    private void HandleMatchChatSlashCommand(IPAddress matchPeerIp, string message)
    {
        var player = _session.TryGetByIp(matchPeerIp);
        if (player is null)
        {
            Console.WriteLine(
                $"[chat] ChatManager slash from {matchPeerIp} but no lobby peer — {message}");
            return;
        }

        Console.WriteLine($"[chat] {player.Name}: {message} (ChatManager command, silent)");
        if (!TryHandleSlashCommand(player, message))
            ReplyPrivate(player, FormatUnknownCommandHelp(message));
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

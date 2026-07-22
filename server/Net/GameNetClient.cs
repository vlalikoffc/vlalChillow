using System.Net;
using LiteNetLib;
using StandChillow.LanServer.Lan;
using StandChillow.LanServer.Net.Lobby;
using StandChillow.LanServer.Net.Match;

namespace StandChillow.LanServer.Net;

public enum ClientProbeState
{
    ConnectingLobby,
    InLobby,
    Searching,
    GameHosting,
    InMatch,
    Disconnected,
}

/// <summary>
/// ConnectAsClient: LiteNetLib join to a real phone host/lobby for protocol learning.
/// On OpGameHostingStateChangedEvent follows into the match channel (UDP 7777) like LanLobbyHelper.qxm.
/// Captures are for analysis only — NEVER paste into dedicated-host reply path.
/// </summary>
public sealed class GameNetClient : IDisposable
{
    private readonly NetManager _manager;
    private readonly EventBasedNetListener _listener;
    private readonly CancellationTokenSource _cts = new();
    private readonly List<Task> _tasks = new();
    private readonly string _captureDir;
    private readonly string _profileName;
    private readonly bool _saveCaptures;
    private int _captureIndex;
    private NetPeer? _peer;
    private bool _joinSent;
    private int? _selfMemberId;
    private GameMatchClient? _match;
    private ClientProbeState _state = ClientProbeState.ConnectingLobby;
    private bool _searching;
    private bool _matchFollowStarted;
    private IPEndPoint? _lobbyEndpoint;
    private readonly IPAddress? _forceMatchIp;
    private readonly IPAddress? _bindAddress;
    private string? _lobbyId;
    private string? _gameModeId;

    public GameNetClient(
        string profileName = "probe",
        bool saveCaptures = true,
        IPAddress? forceMatchIp = null)
    {
        _profileName = profileName;
        _saveCaptures = saveCaptures;
        _forceMatchIp = forceMatchIp;
        _captureDir = Path.Combine(AppContext.BaseDirectory, "captures");
        if (_saveCaptures)
            Directory.CreateDirectory(_captureDir);

        _listener = new EventBasedNetListener();
        _manager = new NetManager(_listener)
        {
            AutoRecycle = true,
            IPv6Enabled = false,
        };

        _listener.PeerConnectedEvent += peer =>
        {
            _peer = peer;
            Console.WriteLine($"[client-net] CONNECTED to {peer.Address}:{peer.Port}");
            SendJoinRequest(peer);
        };
        _listener.PeerDisconnectedEvent += (peer, info) =>
        {
            Console.WriteLine($"[client-net] DISCONNECTED {peer.Address}:{peer.Port}: {info.Reason}");
            SetState(ClientProbeState.Disconnected);
        };
        _listener.NetworkReceiveEvent += OnReceive;
        _listener.NetworkErrorEvent += (ep, error) =>
            Console.WriteLine($"[client-net] error {ep}: {error}");

        _bindAddress = LanInterfacePicker.PickLanBindAddress();
        StartBoundLobby();
        _tasks.Add(Task.Run(PollLoop));
    }

    public int? SelfMemberId => _selfMemberId;
    public ClientProbeState State => _state;
    public GameMatchClient? Match => _match;
    public GameHostingState? LastHosting { get; private set; }
    public IPEndPoint? LobbyEndpoint => _lobbyEndpoint;

    public void Connect(IPEndPoint host)
    {
        _lobbyEndpoint = host;
        Console.WriteLine(
            $"[client-net] Connecting to lobby {host.Address}:{host.Port} " +
            $"key='{GameMatchClient.ConnectionKey}' (phone→host dataLen=17 = Put of same key; " +
            $"lobby Accept() ignores it) …");
        SetState(ClientProbeState.ConnectingLobby);
        // Live phone lobby Connect also sends fwv.cwpw; lobby Accept() ignores key.
        _manager.Connect(host, GameMatchClient.ConnectionKey);
    }

    private void StartBoundLobby()
    {
        bool ok;
        if (_bindAddress is not null)
        {
            ok = _manager.Start(_bindAddress, IPAddress.IPv6Any, 0);
            var ifName = LanInterfacePicker.FindInterfaceName(_bindAddress) ?? "?";
            Console.WriteLine(
                ok
                    ? $"[client-net] LiteNetLib bound LAN {ifName} {_bindAddress}:{_manager.LocalPort} (skip VPN default route)"
                    : $"[client-net] failed to bind LAN {_bindAddress} — falling back to Start()");
            if (!ok)
                ok = _manager.Start();
        }
        else
        {
            Console.WriteLine("[client-net] no LAN NIC for bind — Start() on default");
            ok = _manager.Start();
        }

        if (!ok)
            throw new InvalidOperationException("Failed to start LiteNetLib client");
    }

    private async Task PollLoop()
    {
        while (!_cts.IsCancellationRequested)
        {
            _manager.PollEvents();
            try { await Task.Delay(15, _cts.Token); }
            catch (OperationCanceledException) { break; }
        }
    }

    private void SendJoinRequest(NetPeer peer)
    {
        if (_joinSent) return;
        _joinSent = true;
        // Built from known auth/profile structure — not a captured blob replay.
        var payload = LobbyCodec.BuildJoinRequest(_profileName, avatar: null);
        peer.Send(payload, DeliveryMethod.ReliableSequenced);
        Console.WriteLine(
            $"[client-lobby] TX OpJoinLobbyRequest name='{_profileName}' avatar=0 " +
            $"ver={LobbyAuth.VersionHash[..8]}… auth={LobbyAuth.AuthHash[..8]}… len={payload.Length}");
        DumpCapture("client_join_req", payload);
    }

    private void OnReceive(NetPeer peer, NetPacketReader reader, byte channel, DeliveryMethod method)
    {
        var len = reader.AvailableBytes;
        var payload = reader.GetRemainingBytes();
        try
        {
            DecodeHostOp(payload);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[client-lobby] decode error len={len}: {ex.Message}");
            DumpCapture("client_err", payload);
        }
    }

    private void DecodeHostOp(byte[] payload)
    {
        if (payload.Length == 0) return;
        var r = new LobbyReader(payload);
        var opcode = (LobbyOpcode)r.ReadByte();
        Console.WriteLine($"[client-lobby] RX op={opcode} ({(byte)opcode}) len={payload.Length}");
        DumpCapture($"client_op{(byte)opcode}", payload);

        switch (opcode)
        {
            case LobbyOpcode.OpJoinLobbyResponse:
            {
                var resp = LobbyCodec.ParseJoinResponse(r);
                if (!resp.Success)
                {
                    Console.WriteLine($"[client-lobby] Join FAIL reason={resp.FailReason}");
                    break;
                }
                _selfMemberId = resp.SelfMemberId;
                var snap = resp.Snapshot!;
                Console.WriteLine(
                    $"[client-lobby] Join OK selfId={resp.SelfMemberId} lobby='{snap.LobbyName}' " +
                    $"max={snap.MaxMembers} members={snap.Members.Count} hosting={snap.HasHosting}" +
                    (snap.HasHosting ? $" {snap.HostIp}:{snap.HostPort}" : ""));
                foreach (var m in snap.Members)
                {
                    Console.WriteLine(
                        $"  member id={m.Id} name='{m.Name}' avatar={(m.Avatar?.Length ?? 0)}");
                }
                foreach (var (key, val) in snap.Props)
                    Console.WriteLine($"  prop {key}={val}");
                _lobbyId = ExtractLobbyId(snap.Props);
                _gameModeId = ExtractGameModeId(snap.Props);
                if (_lobbyId is not null)
                    Console.WriteLine(
                        $"[client-lobby] LobbyId='{_lobbyId}' (fallback JoinRoom room candidate; " +
                        $"live cwgt uses roster nick '{_profileName}')");
                else
                    Console.WriteLine(
                        $"[client-lobby] LobbyId prop missing — JoinRoom primary room='{_profileName}'");
                if (_gameModeId is not null)
                    Console.WriteLine($"[client-lobby] GameModeId='{_gameModeId}'");
                SetState(ClientProbeState.InLobby);

                // Mid-match / game-in-progress: etm.hasHosting can embed match IP:port.
                // Live pre-match JoinResponse had hasHosting=0; if a host sets it for late
                // joiners, follow the same path as op9 (do not invent — only act when present).
                if (snap.HasHosting
                    && !string.IsNullOrWhiteSpace(snap.HostIp)
                    && snap.HostPort != 0)
                {
                    Console.WriteLine(
                        "[client-lobby] JoinResponse hasHosting set — mid-match join path " +
                        $"(etm embedded {snap.HostIp}:{unchecked((ushort)snap.HostPort)})");
                    BeginMatchConnect(new GameHostingState(
                        true, snap.HostIp, unchecked((ushort)snap.HostPort)));
                }
                else
                {
                    Console.WriteLine(
                        "[client-lobby] JoinResponse hasHosting=false — if match already running, " +
                        "wait for late OpGameHostingStateChanged (op9) / SearchingStarted");
                }
                break;
            }
            case LobbyOpcode.OpLobbyNewMemberEvent:
            {
                var m = LobbyCodec.ParseMember(r);
                Console.WriteLine($"[client-lobby] NewMember id={m.Id} name='{m.Name}' avatar={(m.Avatar?.Length ?? 0)}");
                break;
            }
            case LobbyOpcode.OpLobbyMemberLeftEvent:
            {
                var id = LobbyCodec.ParseMemberLeft(r);
                Console.WriteLine($"[client-lobby] MemberLeft id={id}");
                break;
            }
            case LobbyOpcode.OpChatMessageEvent:
            {
                var (senderId, msg) = LobbyCodec.ParseChatEvent(r);
                Console.WriteLine($"[client-lobby] Chat from id={senderId}: {msg}");
                break;
            }
            case LobbyOpcode.OpLobbyPropertyChangedEvent:
            {
                var ch = LobbyCodec.ParsePropertyChanged(r);
                Console.WriteLine($"[client-lobby] PropChanged kind={ch.Kind} key={ch.Key ?? "(none)"} value={ch.Value}");
                HandleSearchingProp(ch);
                break;
            }
            case LobbyOpcode.OpGameHostingStateChangedEvent:
            {
                var host = LobbyCodec.ParseGameHostingState(r);
                LastHosting = host;
                Console.WriteLine($"[client-lobby] GameHostingState → {host}");
                if (host.HasHosting && host.Ip is not null)
                {
                    Console.WriteLine(
                        "[client-lobby] op9 hosting — follow into match " +
                        "(Play path or mid-match late-join push)");
                    BeginMatchConnect(host);
                }
                else
                    Console.WriteLine("[state] game hosting cleared (still lobby)");
                break;
            }
            case LobbyOpcode.OpLobbyMemberPropertyChangedEvent:
            case LobbyOpcode.OpHaveBeenKickedEvent:
                Console.WriteLine($"[client-lobby] {opcode} (body captured; structured decode TBD)");
                if (r.Remaining > 0)
                    Console.WriteLine($"[client-lobby]   rem={r.Remaining} head={Convert.ToHexString(payload.AsSpan(1, Math.Min(r.Remaining, 64)))}");
                break;
            default:
                Console.WriteLine($"[client-lobby] unexpected/unknown opcode {(byte)opcode}");
                break;
        }

        if (r.Remaining > 0 && opcode is LobbyOpcode.OpJoinLobbyResponse
            or LobbyOpcode.OpLobbyNewMemberEvent
            or LobbyOpcode.OpLobbyMemberLeftEvent
            or LobbyOpcode.OpChatMessageEvent
            or LobbyOpcode.OpLobbyPropertyChangedEvent
            or LobbyOpcode.OpGameHostingStateChangedEvent)
            Console.WriteLine($"[client-lobby] warning: {r.Remaining} trailing bytes after {opcode}");
    }

    private void HandleSearchingProp(LobbyPropertyChanged ch)
    {
        if (ch.Kind == LobbyPropKind.CustomProperty
            && string.Equals(ch.Key, LobbyPropKeys.SearchingStarted, StringComparison.Ordinal)
            && ch.Value.Kind == LobbyVariantKind.Bool)
        {
            _searching = ch.Value.Bool;
            SetState(_searching ? ClientProbeState.Searching : ClientProbeState.InLobby);
            return;
        }

        if (ch.Kind == LobbyPropKind.CustomProperties && ch.Value.Kind == LobbyVariantKind.Properties)
        {
            foreach (var (k, v) in ch.Value.Props ?? Array.Empty<(string, LobbyVariant)>())
                Console.WriteLine($"[client-lobby]   batch {k}={v}");
        }
    }

    private void BeginMatchConnect(GameHostingState host)
    {
        if (_matchFollowStarted)
        {
            Console.WriteLine(
                "[client-lobby] match follow already started — ignore duplicate hosting signal");
            return;
        }

        if (_lobbyEndpoint is null)
        {
            Console.WriteLine("[client-lobby] no lobby endpoint remembered — cannot resolve match IP");
            return;
        }

        MatchEndpointResolver.Resolution resolved;
        try
        {
            resolved = MatchEndpointResolver.Resolve(host, _lobbyEndpoint, _forceMatchIp);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[client-lobby] match resolve failed: {ex.Message}");
            return;
        }

        _matchFollowStarted = true;
        SetState(ClientProbeState.GameHosting);
        Console.WriteLine($"[client-lobby] {resolved.Reason}");
        Console.WriteLine(
            $"[client-lobby] following into match like LanLobbyHelper.qxm → {resolved.Endpoint} " +
            $"(lobby stays on {_lobbyEndpoint.Port}; game port live={GameMatchClient.DefaultMatchPort}; " +
            $"real client uses op9 IP as-is — we may remap off-LAN ads for PC VPN)");

        try
        {
            _match?.Dispose();
            _match = new GameMatchClient(
                _saveCaptures,
                onConnected: () => SetState(ClientProbeState.InMatch),
                bindAddress: resolved.BindAddress ?? _bindAddress,
                appId: MatchAuth.AppId,
                roomId: _lobbyId,
                gameModeId: _gameModeId,
                // Live fuy.cwgt = participant roster (self nick under illusion).
                // When probing a phone host, use our lobby join name.
                rosterRoom: _profileName);
            Console.WriteLine(
                $"[client-lobby] match JoinRoom wire plan: appId='{MatchAuth.AppId}' " +
                $"password='{GameMatchHost.LanCreateRoomPasswordKey}' mode=JoinOnly " +
                $"room(roster)='{_profileName}' lobbyId='{_lobbyId ?? "(none)"}' " +
                "(after Found: probe → Spectator via SetProperty team FF03)");
            _match.Connect(resolved.Endpoint, resolved.Fallback);
        }
        catch (Exception ex)
        {
            _matchFollowStarted = false;
            Console.WriteLine($"[client-lobby] match connect failed: {ex.Message}");
        }
    }

    /// <summary>
    /// LobbyId from JoinResponse props — fallback JoinRoom candidate only.
    /// Live <c>fuy.cwgt</c> is participant roster (probe nick), not this hash.
    /// </summary>
    private static string? ExtractLobbyId(IReadOnlyList<(string Key, LobbyVariant Value)> props)
    {
        foreach (var (key, val) in props)
        {
            if (!string.Equals(key, LobbyPropKeys.LobbyId, StringComparison.Ordinal))
                continue;
            if (val.Kind == LobbyVariantKind.String && !string.IsNullOrEmpty(val.String))
                return val.String;
        }
        return null;
    }

    private static string? ExtractGameModeId(IReadOnlyList<(string Key, LobbyVariant Value)> props)
    {
        foreach (var (key, val) in props)
        {
            if (!string.Equals(key, LobbyPropKeys.GameModeId, StringComparison.Ordinal))
                continue;
            if (val.Kind == LobbyVariantKind.String && !string.IsNullOrEmpty(val.String))
                return val.String;
        }
        return null;
    }

    private void SetState(ClientProbeState next)
    {
        if (_state == next) return;
        Console.WriteLine($"[state] {_state} → {next}");
        _state = next;
    }

    private void DumpCapture(string tag, byte[] payload)
    {
        if (!_saveCaptures) return;
        try
        {
            var i = Interlocked.Increment(ref _captureIndex);
            var path = Path.Combine(_captureDir, $"{DateTime.UtcNow:yyyyMMdd_HHmmss}_{i:D3}_{tag}_len{payload.Length}.bin");
            File.WriteAllBytes(path, payload);
            Console.WriteLine($"[client-lobby] captured {path}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[client-lobby] capture failed: {ex.Message}");
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        try { Task.WhenAll(_tasks).Wait(800); } catch { /* ignore */ }
        _match?.Dispose();
        _manager.Stop();
        _cts.Dispose();
    }
}

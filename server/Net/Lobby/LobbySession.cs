using LiteNetLib;

namespace StandChillow.LanServer.Net.Lobby;

public enum PlayerLobbyStatus
{
    Lobby,
    InMatch,
}

public sealed class ConnectedPlayer
{
    public required NetPeer Peer { get; init; }
    public required int ServerMemberId { get; init; }
    /// <summary>Member id as presented to this client (always 1 — self, after Server=0).</summary>
    public int ClientLocalMemberId => 1;
    public required string Name { get; set; }
    public byte[]? Avatar { get; set; }
    public PlayerLobbyStatus Status { get; set; } = PlayerLobbyStatus.Lobby;
    public bool Joined { get; set; }
}

/// <summary>
/// Host-side LAN lobby session.
/// Per-client illusion: each joiner only ever sees Server (id 0) + self (id 1),
/// so the client's 4-player LAN cap never triggers while unlimited real peers can connect.
/// </summary>
public sealed class LobbySession
{
    public const string ServerPlayerName = "Server";
    public const int ServerMemberId = 0;
    public const byte ClientVisibleMaxMembers = 4;
    public const string WelcomeMessage = "это тест ради нехуй делать";

    private readonly object _gate = new();
    private readonly Dictionary<NetPeer, ConnectedPlayer> _byPeer = new();
    private readonly List<ConnectedPlayer> _order = new();
    private int _nextMemberId = 1;
    private string _lobbyName;
    private readonly string _lobbyId;
    private string _gameModeId = LobbyPropKeys.DefaultGameModeId;
    private IReadOnlyList<string> _selectedLevels = new[] { LobbyPropKeys.DefaultSelectedLevel };
    private bool _joinable = true;
    private bool _gameInProgress;
    private bool _searching;

    public LobbySession(string lobbyName, string? lobbyId = null)
    {
        _lobbyName = lobbyName;
        // Real phone: discovery HostName == LobbyId property (32-char uppercase MD5 hex).
        _lobbyId = string.IsNullOrWhiteSpace(lobbyId)
            ? LobbyAuth.Md5Hex(Guid.NewGuid().ToString("N"))
            : lobbyId.Trim().ToUpperInvariant();
    }

    public string LobbyName
    {
        get { lock (_gate) return _lobbyName; }
        set { lock (_gate) _lobbyName = value; }
    }

    /// <summary>LobbyId custom prop + discovery HostName (live phone uses same hash).</summary>
    public string LobbyId
    {
        get { lock (_gate) return _lobbyId; }
    }

    public string GameModeId
    {
        get { lock (_gate) return _gameModeId; }
        set { lock (_gate) _gameModeId = value; }
    }

    public IReadOnlyList<string> SelectedLevels
    {
        get { lock (_gate) return _selectedLevels; }
        set
        {
            lock (_gate)
                _selectedLevels = value is { Count: > 0 }
                    ? value.ToList()
                    : new List<string> { LobbyPropKeys.DefaultSelectedLevel };
        }
    }

    public bool Searching
    {
        get { lock (_gate) return _searching; }
        set { lock (_gate) _searching = value; }
    }

    public bool Joinable
    {
        get { lock (_gate) return _joinable; }
        set { lock (_gate) _joinable = value; }
    }

    public bool GameInProgress
    {
        get { lock (_gate) return _gameInProgress; }
        set
        {
            lock (_gate)
            {
                _gameInProgress = value;
                foreach (var p in _order)
                    if (p.Joined)
                        p.Status = value ? PlayerLobbyStatus.InMatch : PlayerLobbyStatus.Lobby;
            }
        }
    }

    public int JoinedCount
    {
        get { lock (_gate) return _order.Count(p => p.Joined); }
    }

    public IReadOnlyList<(string Key, LobbyVariant Value)> BuildLobbyProps()
    {
        lock (_gate)
        {
            // Dedicated defaults: Ranked2v2 + Sandstone 2x2 (live «союзники» capture).
            // Phone JoinResponse often starts DeathMatch then op7; we ship mode+levels in snapshot.
            return new List<(string, LobbyVariant)>
            {
                (LobbyPropKeys.LobbyId, LobbyVariant.FromString(_lobbyId)),
                (LobbyPropKeys.GameModeId, LobbyVariant.FromString(_gameModeId)),
                (LobbyPropKeys.SelectedLevels, LobbyVariant.FromStrings(_selectedLevels)),
            };
        }
    }

    /// <summary>op7 CustomProperties bag — same shape as live Ranked2v2 + Sandstone mode change.</summary>
    public IReadOnlyList<(string Key, LobbyVariant Value)> BuildModeLevelProps()
    {
        lock (_gate)
        {
            return new List<(string, LobbyVariant)>
            {
                (LobbyPropKeys.GameModeId, LobbyVariant.FromString(_gameModeId)),
                (LobbyPropKeys.SelectedLevels, LobbyVariant.FromStrings(_selectedLevels)),
            };
        }
    }

    public IReadOnlyList<(string Name, PlayerLobbyStatus Status)> SnapshotRoster()
    {
        lock (_gate)
        {
            var list = new List<(string, PlayerLobbyStatus)>(_order.Count + 1)
            {
                (ServerPlayerName, _gameInProgress ? PlayerLobbyStatus.InMatch : PlayerLobbyStatus.Lobby)
            };
            foreach (var p in _order.Where(p => p.Joined))
                list.Add((p.Name, p.Status));
            return list;
        }
    }

    public void OnPeerConnected(NetPeer peer)
    {
        lock (_gate)
        {
            if (_byPeer.ContainsKey(peer)) return;
            var p = new ConnectedPlayer
            {
                Peer = peer,
                ServerMemberId = _nextMemberId++,
                Name = "?",
            };
            _byPeer[peer] = p;
            _order.Add(p);
        }
    }

    public ConnectedPlayer? OnPeerDisconnected(NetPeer peer, out string? leftName)
    {
        lock (_gate)
        {
            leftName = null;
            if (!_byPeer.Remove(peer, out var p))
                return null;
            _order.Remove(p);
            if (p.Joined)
                leftName = p.Name;
            return p;
        }
    }

    public JoinResult HandleJoinRequest(
        NetPeer peer,
        JoinLobbyRequest req,
        string? matchHostingIp = null,
        ushort matchHostingPort = 0)
    {
        lock (_gate)
        {
            if (!_byPeer.TryGetValue(peer, out var player))
                return JoinResult.Fail(JoinFailReason.NotJoinable, "unknown peer");

            if (!_joinable)
                return JoinResult.Fail(JoinFailReason.NotJoinable, "not joinable");

            // Dedicated server intentionally does NOT enforce MaxMembersReached (4-player LAN bypass).

            if (!LobbyAuth.HashEquals(req.VersionHash, LobbyAuth.VersionHash))
                return JoinResult.Fail(JoinFailReason.InvalidGameVersion, "version hash mismatch");

            if (!LobbyAuth.HashEquals(req.AuthHash, LobbyAuth.AuthHash))
                return JoinResult.Fail(JoinFailReason.InvalidAuthKey, "auth hash mismatch");

            if (string.IsNullOrWhiteSpace(req.Profile.Name))
                return JoinResult.Fail(JoinFailReason.ProfileValidationFailed, "empty name");

            if (req.Profile.Avatar is { Length: > 2_000_000 })
                return JoinResult.Fail(JoinFailReason.ProfileValidationFailed, "avatar too large");

            player.Name = req.Profile.Name.Trim();
            player.Avatar = req.Profile.Avatar;
            player.Status = _gameInProgress ? PlayerLobbyStatus.InMatch : PlayerLobbyStatus.Lobby;
            player.Joined = true;

            // Policy: Server+self illusion (keep). Real phone JoinResponse is host-only + selfId;
            // we still present Server(0)+self(1) so unlimited peers bypass the client 4-cap.
            var visible = new List<LobbyMember>
            {
                new() { Id = ServerMemberId, Name = ServerPlayerName, Avatar = null },
                new()
                {
                    Id = player.ClientLocalMemberId,
                    Name = player.Name,
                    Avatar = player.Avatar,
                },
            };

            // Mid-match: embed etm.hasHosting (match IP:7777) — same esz shape as op9.
            // Pre-match: hasHosting=0 (live phone JoinResponse).
            string? hostIp = null;
            ushort hostPort = 0;
            if (_gameInProgress
                && !string.IsNullOrWhiteSpace(matchHostingIp)
                && matchHostingPort != 0)
            {
                hostIp = matchHostingIp;
                hostPort = matchHostingPort;
            }

            var response = LobbyCodec.BuildJoinSuccess(
                selfMemberId: player.ClientLocalMemberId,
                lobbyName: _lobbyName,
                maxMembers: ClientVisibleMaxMembers,
                members: visible,
                lobbyProps: BuildLobbyProps(),
                hostingIp: hostIp,
                hostingPort: hostPort);

            return JoinResult.Ok(player, response);
        }
    }

    public ConnectedPlayer? TryGet(NetPeer peer)
    {
        lock (_gate) return _byPeer.TryGetValue(peer, out var p) ? p : null;
    }

    public IReadOnlyList<ConnectedPlayer> JoinedPlayers()
    {
        lock (_gate) return _order.Where(p => p.Joined).ToList();
    }

    public static string FormatStatus(PlayerLobbyStatus s) => s switch
    {
        PlayerLobbyStatus.InMatch => "В матче",
        _ => "Лобби",
    };

    public string BuildPlayerListMessage()
    {
        var roster = SnapshotRoster();
        var sb = new System.Text.StringBuilder();
        sb.Append("Текущие игроки в лобби:");
        foreach (var (name, status) in roster)
        {
            sb.Append('\n');
            sb.Append(name);
            sb.Append('(');
            sb.Append(FormatStatus(status));
            sb.Append(')');
        }
        return sb.ToString();
    }
}

public readonly struct JoinResult
{
    public bool Success { get; init; }
    public JoinFailReason? FailReason { get; init; }
    public string? Detail { get; init; }
    public ConnectedPlayer? Player { get; init; }
    public byte[]? ResponseBytes { get; init; }

    public static JoinResult Ok(ConnectedPlayer player, byte[] response) => new()
    {
        Success = true,
        Player = player,
        ResponseBytes = response,
    };

    public static JoinResult Fail(JoinFailReason reason, string detail) => new()
    {
        Success = false,
        FailReason = reason,
        Detail = detail,
        ResponseBytes = LobbyCodec.BuildJoinFail(reason),
    };
}

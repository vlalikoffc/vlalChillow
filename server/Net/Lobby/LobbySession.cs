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
    /// <summary>Stable lobby member id (Server=0; humans start at 1). Same on the wire for all peers.</summary>
    public required int ServerMemberId { get; init; }
    public required string Name { get; set; }
    public byte[]? Avatar { get; set; }
    public PlayerLobbyStatus Status { get; set; } = PlayerLobbyStatus.Lobby;
    public bool Joined { get; set; }
}

/// <summary>
/// Host-side LAN lobby session. Full roster: JoinResponse + OpLobbyNewMember/Left so every
/// client sees Server + all real peers (phone host ops 4/5 — not the former Server+self illusion).
/// </summary>
public sealed class LobbySession
{
    public const string ServerPlayerName = "Server";
    public const int ServerMemberId = 0;
    /// <summary>Dedicated soft cap for snapshot maxMembers (live phone uses 4).</summary>
    public const byte DedicatedMaxMembers = 16;
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
                // Do not force every peer InMatch — presence is per-player (JoinRoom Dedik).
                if (!value)
                {
                    foreach (var p in _order)
                        if (p.Joined)
                            p.Status = PlayerLobbyStatus.Lobby;
                }
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
                (ServerPlayerName, PlayerLobbyStatus.Lobby)
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

            // Dedicated server intentionally does NOT enforce MaxMembersReached (live phone = 4).

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
            player.Status = PlayerLobbyStatus.Lobby;
            player.Joined = true;

            // Full roster snapshot: Server(0) + every already-joined peer (not self — client
            // maps self via selfMemberId, same as live phone host). Live maxMembers=4; dedicated
            // advertises a higher soft cap so N>4 peers still fit the UI roster.
            var visible = new List<LobbyMember>
            {
                new() { Id = ServerMemberId, Name = ServerPlayerName, Avatar = null },
            };
            foreach (var p in _order.Where(p => p.Joined && !ReferenceEquals(p, player)))
                visible.Add(ToLobbyMember(p));

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
                selfMemberId: player.ServerMemberId,
                lobbyName: _lobbyName,
                maxMembers: DedicatedMaxMembers,
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

    public ConnectedPlayer? TryGetByIp(System.Net.IPAddress ip)
    {
        lock (_gate)
        {
            foreach (var p in _order)
            {
                if (!p.Joined || p.Peer.Address is null) continue;
                if (p.Peer.Address.Equals(ip))
                    return p;
            }
            return null;
        }
    }

    public IReadOnlyList<ConnectedPlayer> JoinedPlayers()
    {
        lock (_gate) return _order.Where(p => p.Joined).ToList();
    }

    /// <summary>Wire member for OpLobbyNewMemberEvent / JoinResponse rows.</summary>
    public static LobbyMember ToLobbyMember(ConnectedPlayer p) => new()
    {
        Id = p.ServerMemberId,
        Name = p.Name,
        Avatar = p.Avatar,
    };

    public bool TrySetPlayerStatus(ConnectedPlayer player, PlayerLobbyStatus status)
    {
        lock (_gate)
        {
            if (!player.Joined) return false;
            if (player.Status == status) return false;
            player.Status = status;
            return true;
        }
    }

    public void ResetAllToLobby()
    {
        lock (_gate)
        {
            foreach (var p in _order)
                if (p.Joined)
                    p.Status = PlayerLobbyStatus.Lobby;
        }
    }

    public static string FormatStatus(PlayerLobbyStatus s) => s switch
    {
        PlayerLobbyStatus.InMatch => "В матче",
        _ => "Лобби",
    };

    /// <summary>Full roster text (Server + all real members) with lobby/match presence.</summary>
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

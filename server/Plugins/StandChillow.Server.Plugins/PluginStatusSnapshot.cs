namespace StandChillow.Server.Plugins;

/// <summary>
/// DTO aligned with tgbot <c>standchillow.status.v1</c>
/// (<c>/home/vlal/tgbot/deploy/STANDCHILLOW_TELEMETRY.md</c>).
/// Serializes with System.Text.Json snake_case property names via attributes.
/// </summary>
public sealed class PluginStatusSnapshot
{
    public string Schema { get; set; } = "standchillow.status.v1";
    public string TsUtc { get; set; } = "";
    public bool Running { get; set; } = true;
    public string ServerName { get; set; } = "";
    public string LobbyId { get; set; } = "";
    public bool MatchStarted { get; set; }
    public string AdvertiseIp { get; set; } = "";
    public PluginPortsSnapshot Ports { get; set; } = new();
    public PluginModeSnapshot Mode { get; set; } = new();
    public string Map { get; set; } = "";
    public string? Phase { get; set; }
    public int Round { get; set; }
    public double? MatchDurationSec { get; set; }
    public double? MatchStartedTs { get; set; }
    public Dictionary<string, int> Scores { get; set; } = new();
    public Dictionary<string, PluginTeamSnapshot> Teams { get; set; } = new();
    public List<PluginLobbyRosterEntry> LobbyRoster { get; set; } = new();
}

public sealed class PluginPortsSnapshot
{
    public int Discovery { get; set; } = 5056;
    public int Lobby { get; set; } = 7778;
    public int Match { get; set; } = 7777;
}

public sealed class PluginModeSnapshot
{
    public string Id { get; set; } = "";
    public string Display { get; set; } = "";
}

public sealed class PluginTeamSnapshot
{
    public string Name { get; set; } = "";
    public int Score { get; set; }
    public List<PluginPlayerSnapshot> Players { get; set; } = new();
}

public sealed class PluginPlayerSnapshot
{
    public byte Nr { get; set; }
    public string Name { get; set; } = "";
    public string Team { get; set; } = "";
    public int Kills { get; set; }
    public int Assists { get; set; }
    public int Score { get; set; }
    public int Money { get; set; }
    public bool Dead { get; set; }
    public int Ping { get; set; } = -1;
    public bool IsHost { get; set; }
}

public sealed class PluginLobbyRosterEntry
{
    public string Name { get; set; } = "";
    public string Status { get; set; } = "";
}

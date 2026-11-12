using StandChillow.LanServer.Net.Match;

namespace StandChillow.LanServer.Dashboard;

/// <summary>
/// Everything one frame needs, captured once per render tick.
/// </summary>
/// <remarks>
/// Splitting "read host state" (<see cref="CliDashboard"/>) from "draw the screen"
/// (<see cref="DashboardView"/>) keeps the renderer pure: the same model can be rendered in a
/// unit test without a socket, a terminal or a host. It also removes the old habit of the
/// renderer calling back into the match host several times per frame while the network thread
/// was mutating it.
/// </remarks>
internal sealed class DashboardModel
{
    /// <summary>One roster row in the lobby view.</summary>
    public readonly record struct RosterRow(string Name, string Status, bool IsHost, bool InMatch);

    /// <summary>One kill-feed row.</summary>
    public sealed record FeedLine(
        string KillerName, MatchTeam KillerTeam, bool KillerKnown,
        string VictimName, MatchTeam VictimTeam, DateTime WhenUtc);

    public int Width { get; set; } = 120;

    public int Height { get; set; } = 40;

    public DateTime NowUtc { get; set; } = DateTime.UtcNow;

    public string LobbyName { get; set; } = "";

    public string LobbyId { get; set; } = "";

    public string Mode { get; set; } = "";

    public string Map { get; set; } = "";

    public int Players { get; set; }

    public int Peers { get; set; }

    public bool MatchStarted { get; set; }

    public bool InputRedirected { get; set; }

    public string? Notice { get; set; }

    public DateTime NoticeUtc { get; set; }

    public IReadOnlyList<RosterRow> Roster { get; set; } = Array.Empty<RosterRow>();

    // ── match view ───────────────────────────────────────────────────────────
    public MatchSnapshot? Snapshot { get; set; }

    public IReadOnlyList<MatchActorSnapshot> Actors { get; set; } = Array.Empty<MatchActorSnapshot>();

    public IReadOnlyDictionary<byte, int> DeathsByActor { get; set; } = new Dictionary<byte, int>();

    public DashboardHub.RoundResultEntry? Banner { get; set; }

    public IReadOnlyList<FeedLine> KillFeed { get; set; } = Array.Empty<FeedLine>();

    // ── shared panels ────────────────────────────────────────────────────────
    public IReadOnlyList<DashboardHub.ChatEntry> Chat { get; set; } = Array.Empty<DashboardHub.ChatEntry>();

    public IReadOnlyList<DashboardLogSink.Entry> Log { get; set; } = Array.Empty<DashboardLogSink.Entry>();

    public string InputBuffer { get; set; } = "";

    /// <summary>Two-column layout is used from this width upwards.</summary>
    public bool Wide => Width >= 104;

    /// <summary>Terminal height is comfortable enough for the log panel.</summary>
    public bool RoomForLog => Height >= 34;
}

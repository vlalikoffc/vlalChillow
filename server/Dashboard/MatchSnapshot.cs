using StandChillow.LanServer.Net.Match;

namespace StandChillow.LanServer.Dashboard;

/// <summary>Display-only snapshot of the active match room for the CLI scoreboard.</summary>
public sealed class MatchSnapshot
{
    public string GameModeId { get; init; } = "";
    public string Map { get; init; } = "";
    public byte C2 { get; init; }
    public MatchFlowPhase Phase { get; init; }
    public int Round { get; init; }
    public int ScoreTr { get; init; }
    public int ScoreCt { get; init; }
    public bool IsDeathMatch { get; init; }
    /// <summary>
    /// Room <c>Time</c> prop as published (seconds). Live/BombPlanted = deadline;
    /// PreStart/Prep/RoundEnd = often <c>now</c> (not a countdown) — see MATCH_PHASES_TIMERS.
    /// </summary>
    public double TimeDeadline { get; init; }
    /// <summary>
    /// Host phase timer end (UTC). CLI countdown prefers this over <see cref="TimeDeadline"/>
    /// so Prep (Time=now) does not show a bogus 00:00 / Live clock.
    /// </summary>
    public DateTime PhaseEndsUtc { get; init; }
    /// <summary>
    /// Allies buy: true while host is holding <c>BuyEndGrace</c> after client-zero (before Live).
    /// </summary>
    public bool AlliesBuyEndGraceArmed { get; init; }
    public IReadOnlyList<MatchActorSnapshot> Actors { get; init; } = Array.Empty<MatchActorSnapshot>();
}

/// <summary>One actor row for the scoreboard (values are host-tracked props, never invented).</summary>
public sealed class MatchActorSnapshot
{
    public byte Nr { get; init; }
    public string Name { get; init; } = "";
    public MatchTeam Team { get; init; }
    public int Money { get; init; }
    public int Kills { get; init; }
    public int Assists { get; init; }
    public int Score { get; init; }
    /// <summary>Round death flag (non-zero = dead this round). Cumulative D is dashboard-derived.</summary>
    public bool DeadThisRound { get; init; }
    /// <summary>Server-measured ping (RTT ms); -1 when unknown.</summary>
    public int Ping { get; init; }
    /// <summary>The synthetic dedicated host actor ("Server", master/spectator).</summary>
    public bool IsHost { get; init; }
}

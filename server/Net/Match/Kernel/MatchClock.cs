namespace StandChillow.LanServer.Net.Match.Kernel;

/// <summary>
/// Single owner of phase deadlines for a room. Modes schedule intervals from
/// evidence-labeled constants (see <c>MatchFlowTestParams</c> / mode READMEs);
/// they must not invent wire durations into live replies.
/// </summary>
public sealed class MatchClock
{
    public DateTime PhaseEndsUtc { get; private set; } = DateTime.MinValue;

    public void Schedule(TimeSpan duration, DateTime? nowUtc = null)
    {
        PhaseEndsUtc = (nowUtc ?? DateTime.UtcNow) + duration;
    }

    public void HoldForever() => PhaseEndsUtc = DateTime.MaxValue;

    public bool IsExpired(DateTime? nowUtc = null) =>
        (nowUtc ?? DateTime.UtcNow) >= PhaseEndsUtc;

    /// <summary>Sync from legacy <see cref="MatchFlowState.PhaseEndsUtc"/> during migration.</summary>
    public void MirrorFrom(MatchFlowState flow) => PhaseEndsUtc = flow.PhaseEndsUtc;

    public void WriteThrough(MatchFlowState flow) => flow.PhaseEndsUtc = PhaseEndsUtc;
}

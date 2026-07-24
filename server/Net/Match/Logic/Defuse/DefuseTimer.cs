namespace StandChillow.LanServer.Net.Match.Logic.Defuse;

/// <summary>
/// Shared bomb fuse / phase-deadline math for defuse-family modes (Escalation, Allies, Ranked).
/// Source of truth for fuse countdown: Escalation phone-host probe
/// (<c>MATCH_ESCALATION_PROBE.md</c>) — explode only at <c>plantUtc + BombFuse</c>; never invent
/// plantUtc or re-anchor from <c>PhaseEndsUtc</c> alone.
/// Modes keep C2 bags / phase enums; this owns timer arithmetic and optional buy pad/grace.
/// </summary>
public static class DefuseTimer
{
    /// <summary>Default fuse length — <see cref="MatchHostSettings.BombFuse"/> (~40s gold).</summary>
    public static TimeSpan DefaultFuse => MatchHostSettings.BombFuse;

    /// <summary>UTC moment the fuse expires given a real plant timestamp.</summary>
    public static DateTime FuseEndsUtc(DateTime plantUtc, TimeSpan? fuse = null) =>
        plantUtc + (fuse ?? DefaultFuse);

    /// <summary>
    /// Tick fuse against a proven <paramref name="plantUtc"/>.
    /// Caller syncs <c>PhaseEndsUtc</c> to <paramref name="fuseEndUtc"/> while Running.
    /// </summary>
    public static FuseTickResult TickFuse(
        DateTime plantUtc,
        DateTime nowUtc,
        out DateTime fuseEndUtc,
        out double elapsedSeconds,
        TimeSpan? fuse = null)
    {
        fuseEndUtc = FuseEndsUtc(plantUtc, fuse);
        elapsedSeconds = (nowUtc - plantUtc).TotalSeconds;
        return nowUtc >= fuseEndUtc ? FuseTickResult.Expired : FuseTickResult.Running;
    }

    /// <summary>
    /// Wire room <c>Time</c> deadline (ServerTime seconds) for fuse UI — Escalation auto-plant /
    /// Ranked plant pattern: <c>nowSec + fuseSec</c>.
    /// </summary>
    public static double FuseWireDeadlineSec(double nowServerSec, TimeSpan? fuse = null) =>
        nowServerSec + (fuse ?? DefaultFuse).TotalSeconds;

    /// <summary>Arm a wall-clock phase end: <c>now + duration</c>.</summary>
    public static DateTime ArmDeadline(TimeSpan duration, DateTime? nowUtc = null) =>
        (nowUtc ?? DateTime.UtcNow) + duration;

    /// <summary>
    /// Buy/prep wire <c>Time</c> with optional client clock pad.
    /// Escalation uses pad=0 (wire = now + phase). Allies on this LAN build needs
    /// <paramref name="clientClockPadSec"/> so bfqt lag does not show ~19s for a 10s buy.
    /// </summary>
    public static double BuyWireDeadlineSec(
        double nowServerSec,
        TimeSpan buyPhase,
        double clientClockPadSec = 0) =>
        nowServerSec + buyPhase.TotalSeconds - clientClockPadSec;

    /// <summary>
    /// Absolute ServerTime when the client-visible buy countdown hits 0
    /// (= now + buyPhase; independent of pad).
    /// </summary>
    public static double BuyClientZeroSec(double nowServerSec, TimeSpan buyPhase) =>
        nowServerSec + buyPhase.TotalSeconds;

    /// <summary>
    /// Wall seconds remaining until client-zero after bag TX.
    /// Tiny remain (&lt;50ms) falls back to full <paramref name="buyPhase"/> (TX skew guard).
    /// </summary>
    public static double RemainToClientZeroSec(
        double clientZeroSec,
        double nowServerSec,
        TimeSpan buyPhase)
    {
        var remain = clientZeroSec - nowServerSec;
        return remain < 0.05 ? buyPhase.TotalSeconds : remain;
    }

    /// <summary>
    /// Two-step post-zero grace (Allies buy→Live checkpoint). First call after client-zero:
    /// sets <paramref name="graceArmed"/>, re-arms <paramref name="phaseEndsUtc"/> for
    /// <paramref name="grace"/>, returns false. Second call after that deadline: returns true.
    /// Escalation has no equivalent — omit or pass <see cref="TimeSpan.Zero"/> only if needed.
    /// </summary>
    public static bool TryArmOrPassPostZeroGrace(
        ref bool graceArmed,
        ref DateTime phaseEndsUtc,
        TimeSpan grace,
        DateTime? nowUtc = null)
    {
        if (graceArmed)
            return true;

        graceArmed = true;
        phaseEndsUtc = (nowUtc ?? DateTime.UtcNow) + grace;
        return false;
    }
}

/// <summary>Outcome of <see cref="DefuseTimer.TickFuse"/>.</summary>
public enum FuseTickResult : byte
{
    Running = 0,
    Expired = 1,
}

using StandChillow.LanServer.Net.Match;

namespace StandChillow.LanServer.Net.Match.Kernel;

/// <summary>
/// The single plant rule for every bomb mode. Pure (no sockets, no rooms) so the decision is
/// regression-tested in CI without binding UDP.
/// </summary>
/// <remarks>
/// <para>
/// <b>Who may plant.</b> The round-start <c>bomberId</c> is <b>not</b> a plant permit. The bomb is a
/// carried world entity: the bomber can drop it, it drops on the bomber's death, and any other
/// attacker can pick it up and plant it. Gating the plant on «planter == assigned bomber» threw
/// those plants away — the planter already applied the plant locally, every other peer got
/// nothing, and the round desynced (issue #33: «у клиента бомба запленчена, а сервер это не
/// принял»). No signature in this file takes a bomber on purpose: no plant decision can consult
/// it.
/// </para>
/// <para>
/// <b>One rule, four modes.</b> <c>Ranked2v2</c>, <c>Ranked2v2Alt</c>, <c>RankedDefuse</c> and
/// <c>Defuse</c> are one bomb family (<see cref="BombFamilyModeIds"/>): they share the Allies gold
/// FSM, the same C2 bags and the same host combat clock. <see cref="IsBombFamilyMode"/> is the only
/// place that list exists, so a rule written once is the rule all four get — there is no per-mode
/// branch to drift.
/// </para>
/// <para>
/// <b>What still holds</b>, because it is a rule of the round and not an identity check:
/// </para>
/// <list type="bullet">
/// <item>one plant per round — a second <c>C2=40</c> would hand clients a second bomb;</item>
/// <item>combat phase only — the bomb family plants during <c>C2=31</c> (<c>PurchasePhase</c>),
/// the classic Ranked/Escalation loop during <c>RoundLive</c>;</item>
/// <item>attackers only — a defender planting is not a handoff, it is a bad packet.</item>
/// </list>
/// </remarks>
public static class MatchPlantPolicy
{
    /// <summary>
    /// Wire <c>C0</c> ids that run the shared bomb round (Allies gold FSM):
    /// <c>C2=22→31→40?→101</c>, host combat timeout, plant during <c>C2=31</c>.
    /// </summary>
    public static readonly IReadOnlyList<string> BombFamilyModeIds = new[]
    {
        "Ranked2v2",      // 2×2 «Союзники»
        "Ranked2v2Alt",   // 2×2 «Союзники: Альтернатива»
        "RankedDefuse",   // Соревновательный
        "Defuse",         // Обезвреживание
    };

    /// <summary>
    /// True for every mode that plants through the shared bomb-family path. This is the single
    /// source for the mode list — the host's <c>IsAlliesFamilyModeId</c> delegates here.
    /// </summary>
    public static bool IsBombFamilyMode(string? modeId) =>
        modeId is { Length: > 0 }
        && BombFamilyModeIds.Contains(modeId, StringComparer.Ordinal);

    /// <summary>Why a plant Rpc was refused. <see cref="None"/> means «accepted».</summary>
    public enum Deny : byte
    {
        None = 0,
        /// <summary>This round already has a live plant (or a pending one).</summary>
        AlreadyPlanted = 1,
        /// <summary>The round is already ending (wipe / defuse / explode in flight).</summary>
        RoundEnding = 2,
        /// <summary>The round is over — round-end pause or match over.</summary>
        RoundOver = 3,
        /// <summary>Wrong phase for a plant (<c>C2=31</c> for the family, Live for classic).</summary>
        Phase = 4,
        /// <summary>The sender is a known defender / spectator, not a bomb carrier.</summary>
        PlanterTeam = 5,
    }

    /// <summary>
    /// Bomb family plants during the <c>C2=31</c> combat phase; the classic Ranked/Escalation
    /// loop plants during <c>C2=101</c> Live.
    /// </summary>
    public static bool AllowsPlantInPhase(MatchFlowPhase phase, bool alliesFamily) =>
        alliesFamily
            ? phase == MatchFlowPhase.PurchasePhase
            : phase == MatchFlowPhase.RoundLive;

    /// <summary>
    /// Any attacker carrying the bomb may plant — the round-start bomber, a teammate who picked
    /// up a dropped bomb, or a teammate the bomb auto-dropped onto when the bomber died.
    /// <c>actorNr == 0</c> is the host-authored path.
    /// </summary>
    /// <remarks>
    /// An <b>unknown</b> team (<see cref="MatchTeam.None"/>) is accepted: the team prop may not
    /// have landed yet for a mid-round joiner, and refusing there would re-open exactly the
    /// silent-drop hole this policy closes. A <b>known</b> defender or spectator is refused.
    /// </remarks>
    public static bool AllowsPlanterTeam(byte planterActorNr, MatchTeam planterTeam) =>
        planterActorNr == 0
        || planterTeam is MatchTeam.None or MatchTeam.Tr;

    /// <summary>
    /// One place for the whole rule, so every call site denies for the same reason in the same
    /// order. <paramref name="bombPlanted"/> covers both a committed plant and a pending one.
    /// </summary>
    public static Deny Check(
        MatchFlowPhase phase,
        bool alliesFamily,
        byte planterActorNr,
        MatchTeam planterTeam,
        bool bombPlanted,
        bool pendingEnd)
    {
        if (bombPlanted || phase == MatchFlowPhase.BombPlanted)
            return Deny.AlreadyPlanted;
        if (phase is MatchFlowPhase.RoundEndPause or MatchFlowPhase.MatchOver)
            return Deny.RoundOver;
        if (pendingEnd)
            return Deny.RoundEnding;
        if (!AllowsPlantInPhase(phase, alliesFamily))
            return Deny.Phase;
        if (!AllowsPlanterTeam(planterActorNr, planterTeam))
            return Deny.PlanterTeam;
        return Deny.None;
    }

    /// <summary>
    /// Same rule, filled straight from the room clock. Call under the host room gate: it reads
    /// <see cref="MatchFlowState"/> fields that the same gate writes.
    /// </summary>
    public static Deny Judge(
        MatchFlowState flow,
        bool alliesFamily,
        byte planterActorNr,
        MatchTeam planterTeam) =>
        Check(
            flow.Phase,
            alliesFamily,
            planterActorNr,
            planterTeam,
            bombPlanted: flow.BombPlanted || flow.PendingBombPlant,
            pendingEnd: flow.PendingEndReason is not null);

    /// <summary>
    /// The planter is somebody other than the round-start carrier — a teammate who picked the
    /// dropped bomb up, or the one it dropped onto when the bomber died. <b>Logging only</b>: it
    /// never denies anything. Kept here so the comparison lives in one file (the plant entry
    /// point must not read <c>bomberId</c> at all).
    /// </summary>
    public static bool IsHandoff(MatchFlowState flow, byte planterActorNr) =>
        planterActorNr != 0 && planterActorNr != flow.BomberActorNr;

    /// <summary>
    /// What to do with a refused plant Rpc: a <b>wrong-phase</b> one is the round-start reset /
    /// equip (gold relays its pose, never <c>C2=40</c>); every other refusal is dropped so peers
    /// never see a bomb that does not exist.
    /// </summary>
    public static bool RelayPoseOnly(Deny deny) => deny == Deny.Phase;

    /// <summary>Short log tag for a denial; empty for <see cref="Deny.None"/>.</summary>
    public static string Describe(Deny deny) => deny switch
    {
        Deny.AlreadyPlanted => "already planted this round (one bomb/round)",
        Deny.RoundEnding => "round already ending (pending end reason)",
        Deny.RoundOver => "round already over (round-end/match-over phase)",
        Deny.Phase => "wrong phase for a plant",
        Deny.PlanterTeam => "sender is not an attacker (defender/spectator plant)",
        _ => "",
    };
}

/// <summary>
/// What the host did with one plant Rpc. Returned by the single
/// <c>GameMatchHost.TryEnterBombPlanted</c> entry point that every bomb mode uses, so the relay
/// decision (fan-out vs pose-only vs drop) is made in exactly one place.
/// </summary>
public readonly record struct MatchPlantDecision(
    bool Accepted,
    MatchPlantPolicy.Deny Deny,
    MatchFlowPhase FromPhase,
    bool Handoff,
    int FanOutPeers)
{
    /// <summary>Refused, nothing was applied.</summary>
    public static MatchPlantDecision Refused(MatchPlantPolicy.Deny deny, MatchFlowPhase fromPhase) =>
        new(false, deny, fromPhase, Handoff: false, FanOutPeers: 0);

    /// <summary>Refused, but the Rpc pose may still be relayed (round-start reset / equip).</summary>
    public bool RelayPoseOnly => !Accepted && MatchPlantPolicy.RelayPoseOnly(Deny);
}

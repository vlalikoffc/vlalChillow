using StandChillow.LanServer.Net.Match;

namespace StandChillow.LanServer.Net.Match.Kernel;

/// <summary>
/// Who is allowed to author which per-actor property. Pure, so the trust boundary is
/// regression-tested without sockets.
/// </summary>
/// <remarks>
/// <para>
/// <b>Issue #33 — the trust boundary.</b> Every per-actor prop arrives from a client and the host
/// relays it verbatim, so without a check any peer can write <c>death=1</c> or <c>kills</c>++ on
/// <b>another</b> player's actor. That is a direct desync primitive: the victim stays alive on its
/// own client while everyone else is told it died, and the wipe/economy maths diverge for the rest
/// of the round. Gold never shows a client authoring another player's combat counters.
/// </para>
/// <para>
/// <b>The rule.</b> Combat counters (<c>kills</c>, <c>fair_kills</c>, <c>round_kills</c>,
/// <c>death</c>, <c>score</c>, …) may only be written by the actor that owns them, except the
/// synthetic host actor which authors deaths for everyone. Everything else (team, loadout, ping,
/// avatar, identity…) keeps today's behaviour: if a mode needs a stricter rule it gets its own
/// entry here, not a one-off check at the call site.
/// </para>
/// </remarks>
public static class MatchPropAuthority
{
    /// <summary>
    /// Props that describe <b>combat outcome</b> for one actor. A client may write them only for
    /// its own actor; the host actor may write them for anyone.
    /// </summary>
    public static readonly IReadOnlyList<string> CombatOutcomeKeys = new[]
    {
        MatchRoomPropKeys.Kills,
        MatchRoomPropKeys.FairKills,
        MatchRoomPropKeys.RoundKills,
        MatchRoomPropKeys.Death,
        MatchRoomPropKeys.Score2,
        MatchRoomPropKeys.Assists,
    };

    /// <summary>
    /// True when a prop write must be refused: a combat-outcome key for somebody else's actor,
    /// from a peer that is neither the actor nor the host.
    /// </summary>
    /// <param name="senderActorNr">Actor nr that owns the sending match peer.</param>
    /// <param name="propActorNr">Actor the prop write targets (<c>actor</c> field on the wire).</param>
    public static bool RejectsForeignCombatProp(
        byte senderActorNr, byte propActorNr, string key, byte hostActorNr)
    {
        if (!IsCombatOutcomeKey(key))
            return false;
        // The host actor is the room master: it authors deaths/kills for other actors by design.
        if (senderActorNr == hostActorNr)
            return false;
        return propActorNr != senderActorNr;
    }

    public static bool IsCombatOutcomeKey(string key) =>
        CombatOutcomeKeys.Contains(key, StringComparer.Ordinal);

    /// <summary>Log tag: which sender tried to write which actor's counter.</summary>
    public static string DescribeForeignCombatProp(
        byte senderActorNr, byte propActorNr, string key) =>
        $"actor={propActorNr} key='{key}' REJECTED (sender actor={senderActorNr} may not author " +
        "another player's combat counter — #33 trust boundary)";
}

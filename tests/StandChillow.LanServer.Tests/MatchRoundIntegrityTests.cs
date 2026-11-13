using StandChillow.LanServer.Net.Lobby;
using StandChillow.LanServer.Net.Match;
using StandChillow.LanServer.Net.Match.Kernel;
using Xunit;

namespace StandChillow.LanServer.Tests;

/// <summary>
/// Issue #33 — a desync is any place where two clients answer «who is alive / who is in this
/// round» differently. These tests pin the two rules that were asked of the round state:
/// the wipe is judged against the roster frozen for that round, and no peer may author another
/// player's combat counters.
/// </summary>
public sealed class MatchRoundIntegrityTests
{
    private static Dictionary<(byte Actor, string Key), LobbyVariant> Props(
        params (byte Actor, MatchTeam Team, int? Death)[] fighters)
    {
        var props = new Dictionary<(byte, string), LobbyVariant>();
        foreach (var (actor, team, death) in fighters)
        {
            props[(actor, MatchRoomPropKeys.Team)] = LobbyVariant.FromByte((byte)team);
            if (death is int d)
                props[(actor, MatchRoomPropKeys.Death)] = LobbyVariant.FromInt(d);
        }
        return props;
    }

    /// <summary>
    /// The roster is frozen when combat starts, so a stale team prop (player left, never spawned,
    /// rejoined as spectator) cannot fake a wipe on some peers only.
    /// </summary>
    [Fact]
    public void StaleTeamProp_CannotFakeAWipe()
    {
        var props = Props(
            (2, MatchTeam.Tr, null),
            (3, MatchTeam.Ct, null),
            (4, MatchTeam.Ct, null));

        var flow = new MatchFlowState { Phase = MatchFlowPhase.PurchasePhase };
        MatchFlowRules.FreezeRoundRoster(flow, props);
        Assert.Equal(3, flow.RoundRoster.Count); // all three fighters are in this round

        // Actor 4 leaves mid-round — its team prop survives, the round roster does not grow.
        props.Remove((4, MatchRoomPropKeys.Team));
        var (aliveTr, aliveCt, totalTr, totalCt) =
            MatchFlowRules.CountAliveFighters(flow, props);

        Assert.Equal(1, totalTr);
        Assert.Equal(2, totalCt);
        Assert.Equal(1, aliveTr);
        Assert.Equal(2, aliveCt);
    }

    /// <summary>A joiner mid-round is not part of this round and must not create a phantom side.</summary>
    [Fact]
    public void MidRoundJoiner_IsNotCountedInThisRound()
    {
        var props = Props((2, MatchTeam.Tr, null), (3, MatchTeam.Ct, null));
        var flow = new MatchFlowState { Phase = MatchFlowPhase.PurchasePhase };
        MatchFlowRules.FreezeRoundRoster(flow, props);

        props[(5, MatchRoomPropKeys.Team)] = LobbyVariant.FromByte((byte)MatchTeam.Tr);
        var (_, _, totalTr, totalCt) = MatchFlowRules.CountAliveFighters(flow, props);

        Assert.Equal(1, totalTr);
        Assert.Equal(1, totalCt);
    }

    /// <summary>Deaths still count against the frozen roster — a wipe is still a wipe.</summary>
    [Fact]
    public void FrozenRoster_StillResolvesAWipe()
    {
        var props = Props((2, MatchTeam.Tr, 1), (3, MatchTeam.Ct, null));
        var flow = new MatchFlowState { Phase = MatchFlowPhase.PurchasePhase };
        MatchFlowRules.FreezeRoundRoster(flow, props);

        var resolved = MatchFlowRules.TryResolveWipe(
            flow, props, bombPlanted: false, out var winner, out var reason);

        Assert.True(resolved);
        Assert.Equal(MatchTeam.Ct, winner);
        Assert.Equal("wipe-tr", reason);
    }

    /// <summary>Round identity dies with the round: a new round re-freezes it from live props.</summary>
    [Fact]
    public void Roster_IsClearedWithTheRound()
    {
        var flow = new MatchFlowState();
        MatchFlowRules.FreezeRoundRoster(flow, Props((2, MatchTeam.Tr, null)));
        Assert.Single(flow.RoundRoster);

        MatchFlowRules.ClearRoundRoster(flow);
        Assert.Empty(flow.RoundRoster);
    }

    /// <summary>
    /// Only somebody who entered this round may be turned into a death (#33). An unfrozen roster
    /// means «not in combat yet» and accepts everyone, so the guard is safe to call always.
    /// </summary>
    [Fact]
    public void OnlyRoundParticipants_CanBecomeCombatDeaths()
    {
        var flow = new MatchFlowState();
        // Before the freeze the roster is empty and accepts everyone but the host actor.
        Assert.True(MatchFlowRules.IsRoundParticipant(flow, actorNr: 9));

        MatchFlowRules.FreezeRoundRoster(flow, Props((2, MatchTeam.Tr, null), (3, MatchTeam.Ct, null)));
        Assert.True(MatchFlowRules.IsRoundParticipant(flow, actorNr: 2));
        Assert.False(MatchFlowRules.IsRoundParticipant(flow, actorNr: 9));
    }

    [Theory]
    [InlineData(MatchRoomPropKeys.Death)]
    [InlineData(MatchRoomPropKeys.Kills)]
    [InlineData(MatchRoomPropKeys.RoundKills)]
    [InlineData(MatchRoomPropKeys.FairKills)]
    [InlineData(MatchRoomPropKeys.Assists)]
    [InlineData(MatchRoomPropKeys.Score2)]
    public void CombatCounter_FromAnotherActor_IsRejected(string key)
    {
        // sender actor 2 tries to write actor 3's counter
        Assert.True(MatchPropAuthority.RejectsForeignCombatProp(
            senderActorNr: 2, propActorNr: 3, key, hostActorNr: 1));
    }

    [Fact]
    public void OwnCombatCounter_AndHostAuthoredDeath_AreAccepted()
    {
        Assert.False(MatchPropAuthority.RejectsForeignCombatProp(
            senderActorNr: 2, propActorNr: 2, MatchRoomPropKeys.Kills, hostActorNr: 1));
        Assert.False(MatchPropAuthority.RejectsForeignCombatProp(
            senderActorNr: 1, propActorNr: 3, MatchRoomPropKeys.Death, hostActorNr: 1));
    }

    /// <summary>Non-combat props (team, loadout, ping, identity) keep flowing from their actor.</summary>
    [Fact]
    public void OrdinaryActorProps_AreNeverBlocked()
    {
        Assert.False(MatchPropAuthority.RejectsForeignCombatProp(
            senderActorNr: 2, propActorNr: 2, MatchRoomPropKeys.Team, hostActorNr: 1));
        Assert.False(MatchPropAuthority.RejectsForeignCombatProp(
            senderActorNr: 2, propActorNr: 2, MatchRoomPropKeys.Ping, hostActorNr: 1));
        Assert.False(MatchPropAuthority.IsCombatOutcomeKey(MatchRoomPropKeys.Team));
    }

    /// <summary>
    /// Fix #3, exact shape of latest.log round 3: T had two fighters when the round froze, one of
    /// them left mid-round, and the last T was then killed. Freezing <b>before</b> the leaver
    /// walked out keeps the side non-empty, so the death of the last T is a real wipe — not an
    /// «empty roster» case, and not «wait for the bomb» either.
    /// </summary>
    [Fact]
    public void LeaverThenLastFighterDies_ResolvesWipeTr()
    {
        var props = Props(
            (2, MatchTeam.Ct, null),
            (3, MatchTeam.Tr, null),
            (5, MatchTeam.Tr, null));

        var flow = new MatchFlowState { Phase = MatchFlowPhase.PurchasePhase };
        MatchFlowRules.FreezeRoundRoster(flow, props);
        Assert.Equal(3, flow.RoundRoster.Count);

        // Actor 5 leaves mid-round (disconnect removes him from the roster, not from history).
        MatchFlowRules.RemoveFromRoundRoster(flow, actorNr: 5);
        props.Remove((5, MatchRoomPropKeys.Team));

        // The remaining T is eliminated.
        props[(3, MatchRoomPropKeys.Death)] = LobbyVariant.FromInt(1);
        flow.DeadActors.Add(3);

        var resolved = MatchFlowRules.TryResolveWipe(
            flow, props, bombPlanted: false, out var winner, out var reason);

        Assert.True(resolved);
        Assert.Equal(MatchTeam.Ct, winner);
        Assert.Equal("wipe-tr", reason);
    }

    /// <summary>
    /// A mid-round joiner takes a team prop while the round is already running. He is not part of
    /// it (cold start = dead for this round) and must not keep his side alive after a wipe.
    /// </summary>
    [Fact]
    public void MidRoundJoiner_IsNotInTheFrozenRoster()
    {
        var props = Props((2, MatchTeam.Tr, null), (3, MatchTeam.Ct, null));
        var flow = new MatchFlowState { Phase = MatchFlowPhase.PurchasePhase };
        MatchFlowRules.FreezeRoundRoster(flow, props);

        // Joiner arrives after the freeze.
        props[(6, MatchRoomPropKeys.Team)] = LobbyVariant.FromByte((byte)MatchTeam.Tr);

        Assert.False(flow.RoundRoster.ContainsKey(6));
        var (_, _, totalTr, totalCt) = MatchFlowRules.CountAliveFighters(flow, props);
        Assert.Equal(1, totalTr);
        Assert.Equal(1, totalCt);
    }

    /// <summary>
    /// A fighter who dies and switches to Spectator stays in the roster as dead. Spectator means
    /// «not playing right now», not «left the round» — dropping him would empty the side and turn
    /// «everyone is dead» into a different, invented wipe.
    /// </summary>
    [Fact]
    public void DeadFighterAsSpectator_StillCountsAsTheDeadSide()
    {
        var props = Props(
            (2, MatchTeam.Ct, null),
            (3, MatchTeam.Tr, null),
            (5, MatchTeam.Tr, null));
        var flow = new MatchFlowState { Phase = MatchFlowPhase.PurchasePhase };
        MatchFlowRules.FreezeRoundRoster(flow, props);

        // T dies, then the client flips its team prop to Spectator.
        props[(3, MatchRoomPropKeys.Death)] = LobbyVariant.FromInt(1);
        flow.DeadActors.Add(3);
        props[(3, MatchRoomPropKeys.Team)] = LobbyVariant.FromByte((byte)MatchTeam.Spectator);
        props[(5, MatchRoomPropKeys.Team)] = LobbyVariant.FromByte((byte)MatchTeam.Spectator);
        flow.DeadActors.Add(5);

        var (aliveTr, _, totalTr, _) = MatchFlowRules.CountAliveFighters(flow, props);
        Assert.Equal(2, totalTr); // both T are still part of this round
        Assert.Equal(0, aliveTr); // and both are dead → wipe-tr

        var resolved = MatchFlowRules.TryResolveWipe(
            flow, props, bombPlanted: false, out var winner, out var reason);
        Assert.True(resolved);
        Assert.Equal(MatchTeam.Ct, winner);
        Assert.Equal("wipe-tr", reason);
    }

    /// <summary>A leaver leaves the frozen roster, so the round can still be decided.</summary>
    [Fact]
    public void Leaver_IsDroppedFromTheRoster()
    {
        var props = Props((2, MatchTeam.Tr, null), (3, MatchTeam.Ct, null));
        var flow = new MatchFlowState { Phase = MatchFlowPhase.PurchasePhase };
        MatchFlowRules.FreezeRoundRoster(flow, props);

        MatchFlowRules.RemoveFromRoundRoster(flow, actorNr: 3);
        props[(2, MatchRoomPropKeys.Death)] = LobbyVariant.FromInt(0);

        var (_, aliveCt, totalTr, totalCt) = MatchFlowRules.CountAliveFighters(flow, props);
        Assert.Equal(1, totalTr);
        Assert.Equal(0, totalCt);
        Assert.Equal(0, aliveCt);
    }

    /// <summary>
    /// The host actor and actor 0 are never round participants — they must not be turned into
    /// combat deaths even though they may own scene objects.
    /// </summary>
    [Fact]
    public void HostAndZero_AreNeverRoundParticipants()
    {
        var flow = new MatchFlowState();
        Assert.False(MatchFlowRules.IsRoundParticipant(flow, actorNr: 0));
        Assert.False(MatchFlowRules.IsRoundParticipant(flow, actorNr: MatchHostActor.ActorNr));
        Assert.True(MatchFlowRules.IsRoundParticipant(flow, actorNr: 2));
    }
}

using StandChillow.LanServer.Net.Match;
using StandChillow.LanServer.Net.Match.Host;
using Xunit;

namespace StandChillow.LanServer.Tests;

public sealed class ProtocolInvariantTests
{
    [Theory]
    [InlineData(0, true)]  // All: local execution already happened on the sender.
    [InlineData(1, true)]  // Others: explicitly excludes the sender.
    [InlineData(2, true)]  // AllCached: WeaponDrop echo must not return to its owner.
    [InlineData(3, false)] // AllViaServer: sender waits for the host relay.
    [InlineData(4, false)] // AllCachedViaServer: sender waits for the host relay.
    public void WeaponDropRelay_HonoursExceptSenderPolicy(byte gaaTarget, bool excludesSender)
    {
        Assert.Equal(excludesSender, WorldObjectRpcRelayPolicy.ExcludesSender(gaaTarget));
    }

    [Fact]
    public void AlliesCombatTimeout_IsExactly109Seconds()
    {
        Assert.Equal(TimeSpan.FromSeconds(109), AlliesFlowParams.RoundDuration);
        Assert.Equal(TimeSpan.FromSeconds(109), MatchHostSettings.RoundDuration);
    }

    [Fact]
    public void AlliesBuyPhase_IsTenSecondsBetweenC22AndC31()
    {
        Assert.Equal(22, MatchC2States.WarmupWillFinish);
        Assert.Equal(31, MatchC2States.PurchasePhase);
        Assert.Equal(TimeSpan.FromSeconds(10), AlliesFlowParams.BuyPhase);
    }

    [Theory]
    [InlineData("Ranked2v2", 8)]
    [InlineData("Ranked2v2Alt", 8)]
    [InlineData("RankedDefuse", 8)]
    [InlineData("Defuse", 6)]
    [InlineData("Duel", 8)]
    public void WinTargets_MatchTheGoldClientFlows(string modeId, int expectedWins)
    {
        Assert.True(MatchHostSettings.TryDefaultWinsForMode(modeId, out var wins));
        Assert.Equal(expectedWins, wins);
    }

    [Fact]
    public void DuelC2Ladder_MatchesTheGoldClientFlow()
    {
        byte[] ladder =
        [
            MatchC2States.WaitingPlayers,
            MatchC2States.DeathMatchPreWarmup,
            MatchC2States.WarmUp,
            MatchC2States.WarmupWillFinish,
            MatchC2States.PurchasePhase,
            MatchC2States.MatchStarted,
            MatchC2States.FinalHud,
        ];

        Assert.Equal(new byte[] { 10, 11, 21, 22, 31, 101, 201 }, ladder);
    }
}

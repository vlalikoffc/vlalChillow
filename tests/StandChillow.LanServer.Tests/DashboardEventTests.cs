using StandChillow.LanServer.Dashboard;
using StandChillow.LanServer.Net.Match;
using Xunit;

namespace StandChillow.LanServer.Tests;

public sealed class DashboardEventTests
{
    [Fact]
    public void DeathEvent_PreservesVerifiedKillerAttributionForTheKillFeed()
    {
        DashboardHub.DeathEntry? received = null;
        void Handler(DashboardHub.DeathEntry entry) => received = entry;
        DashboardHub.Death += Handler;
        try
        {
            DashboardHub.PostDeath(
                victimNr: 7,
                victimName: "victim",
                victimTeam: MatchTeam.Tr,
                source: "test",
                killerNr: 4,
                killerName: "killer",
                killerTeam: MatchTeam.Ct);
        }
        finally
        {
            DashboardHub.Death -= Handler;
        }

        Assert.NotNull(received);
        Assert.Equal(7, received.VictimNr);
        Assert.Equal("victim", received.VictimName);
        Assert.Equal(4, received.KillerNr);
        Assert.Equal("killer", received.KillerName);
        Assert.Equal(MatchTeam.Ct, received.KillerTeam);
    }
}

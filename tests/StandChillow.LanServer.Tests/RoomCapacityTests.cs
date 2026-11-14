using StandChillow.LanServer.Net.Match;
using StandChillow.LanServer.Net.Match.Kernel;
using Xunit;

namespace StandChillow.LanServer.Tests;

/// <summary>
/// Размер комнаты под режим: «союзники 1 на 1» — два места, а не шестнадцать.
/// </summary>
public sealed class RoomCapacityTests
{
    [Theory]
    [InlineData("Ranked2v2", 1, 2)]
    [InlineData("Ranked2v2", 2, 4)]
    [InlineData("Ranked2v2Alt", 1, 2)]
    [InlineData("Ranked2v2Alt", 2, 4)]
    [InlineData("Duel", 1, 2)]
    [InlineData("Duel", 2, 4)]
    public void DuelLikeModesAreOneOrTwoPerTeam(string mode, int perTeam, int expectedTotal)
    {
        Assert.Equal(expectedTotal, RoomCapacity.TotalFor(mode, perTeam));
    }

    [Theory]
    [InlineData("Defuse", 12, 6)]
    [InlineData("RankedDefuse", 10, 5)]
    public void BombModesHaveFixedTeamSize(string mode, int total, int perTeam)
    {
        var shape = RoomCapacity.ForMode(mode);
        Assert.Equal(total, shape.Total);
        Assert.Equal(perTeam, shape.PerTeam);
        Assert.Empty(shape.Choosable);
    }

    [Fact]
    public void OtherModesHaveNoTeamSplit()
    {
        foreach (var mode in new[] { "DeathMatch", "Escalation", "ArmsRace", "FreeForAll" })
        {
            var shape = RoomCapacity.ForMode(mode);
            Assert.Equal(RoomCapacity.EngineMaxPlayers, shape.Total);
            Assert.Empty(shape.Choosable);
        }
    }

    [Theory]
    [InlineData("Ranked2v2", 1, true, 1)]
    [InlineData("Ranked2v2", 2, true, 2)]
    [InlineData("Ranked2v2", 0, true, 2)]   // 0 — «как в режиме»
    [InlineData("Ranked2v2", 3, true, 2)]   // 3 не бывает: берём ближайший меньший
    [InlineData("Defuse", 6, true, 6)]
    [InlineData("Defuse", 2, false, 6)]     // бомба размер не выбирает
    [InlineData("DeathMatch", 16, true, 16)]
    [InlineData("DeathMatch", 4, false, 16)]
    public void PerTeamIsResolvedOrRejected(string mode, int asked, bool ok, int expected)
    {
        Assert.Equal(ok, RoomCapacity.TryResolvePerTeam(mode, asked, out var result));
        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData("Ranked2v2", 1, "1 на 1")]
    [InlineData("Ranked2v2", 2, "2 на 2")]
    [InlineData("Defuse", 0, "6 на 6")]
    [InlineData("RankedDefuse", 0, "5 на 5")]
    public void LabelsAreHumanReadable(string mode, int perTeam, string expected)
    {
        Assert.Equal(expected, RoomCapacity.Describe(mode, perTeam));
    }
}

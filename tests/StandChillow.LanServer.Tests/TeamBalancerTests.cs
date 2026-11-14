using StandChillow.LanServer.Net.Match;
using StandChillow.LanServer.Net.Match.Kernel;
using Xunit;

namespace StandChillow.LanServer.Tests;

/// <summary>
/// Выравнивание команд: «3 на 1» и «2 на 0» не должны стартовать.
/// </summary>
public sealed class TeamBalancerTests
{
    private static (byte, MatchTeam) A(byte actor, MatchTeam team) => (actor, team);

    [Theory]
    [InlineData(1, 1)]
    [InlineData(2, 2)]
    [InlineData(2, 1)]
    [InlineData(1, 2)]
    public void BalancedRoomNeedsNoChange(int tr, int ct)
    {
        var states = Build(tr, ct);
        Assert.Null(TeamBalancer.Plan(states));
    }

    [Fact]
    public void ThreeAgainstOneIsFixed()
    {
        var states = Build(tr: 3, ct: 1);
        var plan = TeamBalancer.Plan(states);

        Assert.NotNull(plan);
        Assert.Equal(MatchTeam.Tr, plan!.Value.From);
        Assert.Equal(MatchTeam.Ct, plan.Value.To);
        Assert.Single(plan.Value.Actors);
    }

    [Fact]
    public void TwoAgainstZeroIsFixed()
    {
        var states = Build(tr: 2, ct: 0);
        var plan = TeamBalancer.Plan(states);

        Assert.NotNull(plan);
        Assert.Equal(MatchTeam.Tr, plan!.Value.From);
        Assert.Single(plan.Value.Actors);
    }

    [Fact]
    public void FourAgainstZeroMovesTwo()
    {
        var states = Build(tr: 4, ct: 0);
        var plan = TeamBalancer.Plan(states);

        Assert.NotNull(plan);
        Assert.Equal(2, plan!.Value.Actors.Count);
    }

    [Fact]
    public void NewestPlayerIsMoved()
    {
        // Номера актёров растут при входе: последний пришёл — последний и уходит.
        var states = new List<(byte, MatchTeam)>
        {
            A(2, MatchTeam.Tr),
            A(3, MatchTeam.Tr),
            A(4, MatchTeam.Tr),
            A(5, MatchTeam.Ct),
        };

        var plan = TeamBalancer.Plan(states);
        Assert.NotNull(plan);
        Assert.Equal(4, Assert.Single(plan!.Value.Actors));
    }

    [Fact]
    public void CtSurplusMovesToTr()
    {
        var states = Build(tr: 0, ct: 3);
        var plan = TeamBalancer.Plan(states);

        Assert.NotNull(plan);
        Assert.Equal(MatchTeam.Ct, plan!.Value.From);
        Assert.Equal(MatchTeam.Tr, plan.Value.To);
    }

    [Fact]
    public void EmptyRoomNeedsNoChange()
    {
        Assert.Null(TeamBalancer.Plan(Array.Empty<(byte, MatchTeam)>()));
    }

    /// <summary>Собрать состояния: tr человек у Tr (актёры 2..), затем ct у Ct.</summary>
    private static List<(byte, MatchTeam)> Build(int tr, int ct)
    {
        var list = new List<(byte, MatchTeam)>();
        byte actor = 2;
        for (var i = 0; i < tr; i++)
            list.Add(A(actor++, MatchTeam.Tr));
        for (var i = 0; i < ct; i++)
            list.Add(A(actor++, MatchTeam.Ct));
        return list;
    }
}

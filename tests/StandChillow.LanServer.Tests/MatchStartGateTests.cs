using StandChillow.LanServer.Config;
using StandChillow.LanServer.Net;
using Xunit;

namespace StandChillow.LanServer.Tests;

/// <summary>
/// Гейт старта: автостарт по командам и старт по готовности.
/// Сокетов нет — проверяется только решение «стартовать или нет».
/// </summary>
public sealed class MatchStartGateTests
{
    private static ServerConfig Config(MatchStartMode mode, int minPerTeam = 1)
    {
        var cfg = new ServerConfig();
        cfg.MatchStart.Mode = mode switch
        {
            MatchStartMode.Auto => "auto",
            MatchStartMode.Ready => "ready",
            _ => "manual",
        };
        cfg.MatchStart.Auto.MinPlayersPerTeam = minPerTeam;
        return cfg;
    }

    private static MatchStartGate Gate(MatchStartMode mode, int minPerTeam = 1)
    {
        var gate = new MatchStartGate();
        gate.Apply(Config(mode, minPerTeam));
        return gate;
    }

    [Fact]
    public void Manual_NeverStartsOnItsOwn()
    {
        var gate = Gate(MatchStartMode.Manual);

        // Даже полная готовность и полные команды не запускают матч вручную.
        gate.MarkReady("влал");
        gate.MarkReady("другой");

        Assert.False(gate.ShouldStart(new[] { "влал", "другой" }, 5, 5, out var reason));
        Assert.Contains("manual", reason);
    }

    [Fact]
    public void Auto_StartsWhenBothTeamsHaveTheMinimum()
    {
        var gate = Gate(MatchStartMode.Auto, minPerTeam: 1);

        Assert.False(gate.ShouldStart(System.Array.Empty<string>(), 0, 1, out _));
        Assert.False(gate.ShouldStart(System.Array.Empty<string>(), 1, 0, out _));
        Assert.True(gate.ShouldStart(System.Array.Empty<string>(), 1, 1, out _));
    }

    [Fact]
    public void Auto_RespectsAHigherMinimumPerTeam()
    {
        var gate = Gate(MatchStartMode.Auto, minPerTeam: 2);

        Assert.False(gate.ShouldStart(System.Array.Empty<string>(), 1, 1, out _));
        Assert.False(gate.ShouldStart(System.Array.Empty<string>(), 2, 1, out var reason));
        Assert.Contains("CT=2 TR=1", reason);
        Assert.True(gate.ShouldStart(System.Array.Empty<string>(), 2, 2, out _));
    }

    [Fact]
    public void Ready_StartsOnlyWhenEveryoneInMatchIsReady()
    {
        var gate = Gate(MatchStartMode.Ready);
        var inMatch = new[] { "влал", "ботан", "нуб" };

        Assert.False(gate.ShouldStart(inMatch, 2, 1, out _));

        gate.MarkReady("влал");
        Assert.False(gate.ShouldStart(inMatch, 2, 1, out var partial));
        Assert.Contains("1/3", partial);

        gate.MarkReady("ботан");
        gate.MarkReady("нуб");
        Assert.True(gate.ShouldStart(inMatch, 2, 1, out _));
    }

    [Fact]
    public void Ready_PlayerWhoLeftNoLongerBlocksTheStart()
    {
        var gate = Gate(MatchStartMode.Ready);
        var all = new[] { "влал", "ушёл" };

        gate.MarkReady("влал");
        gate.MarkReady("ушёл");
        Assert.True(gate.ShouldStart(all, 1, 1, out _));

        // Игрок вышел из катки — его голос снят, старт считается по оставшимся.
        Assert.True(gate.Remove("ушёл"));

        var left = new[] { "влал" };
        Assert.True(gate.ShouldStart(left, 1, 0, out var reason));
        Assert.Contains("1/1", reason);
    }

    [Fact]
    public void Ready_UnreadyCancelsTheVote()
    {
        var gate = Gate(MatchStartMode.Ready);

        Assert.True(gate.MarkReady("влал"));
        Assert.False(gate.MarkReady("влал")); // повторный /ready ничего не меняет
        Assert.True(gate.IsReady("влал"));

        Assert.True(gate.MarkUnready("влал"));
        Assert.False(gate.MarkUnready("влал"));
        Assert.False(gate.IsReady("влал"));
    }

    [Fact]
    public void Ready_EmptyMatchNeverStarts()
    {
        var gate = Gate(MatchStartMode.Ready);

        Assert.False(gate.ShouldStart(System.Array.Empty<string>(), 0, 0, out var reason));
        Assert.Contains("никого нет", reason);
    }

    [Fact]
    public void Ready_NicknamesAreCaseInsensitive()
    {
        var gate = Gate(MatchStartMode.Ready);

        gate.MarkReady("Влал");
        Assert.True(gate.IsReady("влал"));

        var inMatch = new[] { "влал" };
        Assert.True(gate.ShouldStart(inMatch, 1, 0, out _));
    }

    [Fact]
    public void Clear_DropsEveryVoteBetweenMatches()
    {
        var gate = Gate(MatchStartMode.Ready);

        gate.MarkReady("влал");
        gate.MarkReady("ботан");
        Assert.Equal(2, gate.ReadyCount);

        gate.Clear();
        Assert.Equal(0, gate.ReadyCount);
        Assert.False(gate.IsReady("влал"));
    }

    [Fact]
    public void Apply_SwitchesModeWithoutRestart()
    {
        var gate = Gate(MatchStartMode.Manual);
        Assert.Equal(MatchStartMode.Manual, gate.Mode);

        gate.Apply(Config(MatchStartMode.Ready));
        Assert.Equal(MatchStartMode.Ready, gate.Mode);
    }
}

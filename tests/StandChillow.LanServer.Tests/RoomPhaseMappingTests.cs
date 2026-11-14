using StandChillow.LanServer.Net;
using StandChillow.LanServer.Net.Match;
using StandChillow.LanServer.Net.Match.Kernel;
using Xunit;

namespace StandChillow.LanServer.Tests;

/// <summary>
/// PR 5 плана: как фаза матча превращается в состояние комнаты на панели.
/// Ошибка здесь показывала бы «ожидание» посреди катки или вечную «идёт» после конца.
/// </summary>
public sealed class RoomPhaseMappingTests
{
    [Theory]
    [InlineData(MatchFlowPhase.WaitingPlayers)]
    [InlineData(MatchFlowPhase.Warmup)]
    [InlineData(MatchFlowPhase.WarmupWillFinish)]
    [InlineData(MatchFlowPhase.AlliesPreWarmup)]
    public void PreMatchPhasesAreWaiting(MatchFlowPhase phase)
    {
        // Добрать игроков можно ещё до боя — значит комната открыта.
        Assert.Equal(RoomState.Waiting, GameMatchHost.MapPhase(phase, 1));
    }

    [Theory]
    [InlineData(MatchFlowPhase.PurchasePhase)]
    [InlineData(MatchFlowPhase.RoundLive)]
    [InlineData(MatchFlowPhase.BombPlanted)]
    [InlineData(MatchFlowPhase.RoundEndPause)]
    [InlineData(MatchFlowPhase.HalfTimeIntro)]
    [InlineData(MatchFlowPhase.HalfTimeSwap)]
    [InlineData(MatchFlowPhase.HalfTimeTransition)]
    [InlineData(MatchFlowPhase.DeathMatchWarmup)]
    [InlineData(MatchFlowPhase.DeathMatchLive)]
    public void CombatPhasesAreLive(MatchFlowPhase phase)
    {
        Assert.Equal(RoomState.Live, GameMatchHost.MapPhase(phase, 4));
    }

    [Theory]
    [InlineData(MatchFlowPhase.MatchOver)]
    [InlineData(MatchFlowPhase.DeathMatchEnded)]
    [InlineData(MatchFlowPhase.DeathMatchFinalHud)]
    public void EndPhasesAreFinished(MatchFlowPhase phase)
    {
        Assert.Equal(RoomState.Finished, GameMatchHost.MapPhase(phase, 4));
    }

    [Fact]
    public void EveryPhaseMapsToSomething()
    {
        // Ни одна фаза не должна «провалиться» мимо switch: новая фаза получит Waiting,
        // а не выбросит исключение посреди тика.
        foreach (MatchFlowPhase phase in Enum.GetValues<MatchFlowPhase>())
        {
            var state = GameMatchHost.MapPhase(phase, 0);
            Assert.True(Enum.IsDefined(state));
        }
    }

    [Fact]
    public void PlayerCountDoesNotChangeLiveState()
    {
        // Даже если все вышли посреди раунда, комната «идёт», пока фаза не сменилась:
        // иначе она закрылась бы вместе с боем, который ещё формально жив.
        Assert.Equal(RoomState.Live, GameMatchHost.MapPhase(MatchFlowPhase.RoundLive, 0));
    }
}

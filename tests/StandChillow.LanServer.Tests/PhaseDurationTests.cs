using System.Linq;
using StandChillow.LanServer.Net;
using StandChillow.LanServer.Net.Lobby;
using StandChillow.LanServer.Net.Match;
using Xunit;

namespace StandChillow.LanServer.Tests;

/// <summary>
/// Issue #27 — 2.07 carries the countdown length in the <c>Duration</c> (Double) room prop, and
/// <b>every</b> C2 bag on the allies path must include it. Gold evidence:
/// <c>captures/captures.zip</c> (Ranked2v2 "Sandstone 2x2", session 20261112_0351…0404) —
/// C2=21 → 3.0, C2=22 → 10.0, C2=31 → 110.0, C2=101 → 6.0, C2=111 → 5.0, C2=112 → 1.0,
/// C2=113 → 7.0, C2=201 → 6.0; C2=10/40/255 carry only <c>C2</c>.
/// </summary>
public sealed class PhaseDurationTests
{
    [Fact]
    public void AlliesRoundEndBag_CarriesDurationMatchingThePause()
    {
        var props = GameMatchHost.BuildAlliesRoundEndRoomProps(
            nowSec: 100.0,
            winner: MatchTeam.Tr,
            scoreTr: 1,
            scoreCt: 0,
            coLossesTr: 0,
            coLossesCt: 1,
            GameMatchHost.BuildWinTeamProps(MatchTeam.Tr, mvpNr: 4, mvpCode: 3),
            durationSec: 6.0);

        var duration = Single(props, MatchRoomPropKeys.Duration);
        Assert.Equal(LobbyVariantKind.Double, duration.Kind);
        Assert.Equal(6.0, duration.Double, precision: 6);
    }

    [Fact]
    public void AlliesRoundEndBag_DurationDefaultsToTheGoldSixSeconds()
    {
        var props = GameMatchHost.BuildAlliesRoundEndRoomProps(
            nowSec: 100.0,
            winner: MatchTeam.Ct,
            scoreTr: 0,
            scoreCt: 1,
            coLossesTr: 1,
            coLossesCt: 0,
            GameMatchHost.BuildWinTeamProps(MatchTeam.Ct, mvpNr: 0, mvpCode: 0));

        Assert.Equal(6.0, Single(props, MatchRoomPropKeys.Duration).Double, precision: 6);
    }

    [Fact]
    public void AlliesPhaseTimers_MatchTheGoldDurationValues()
    {
        // Gold C2=21/22/31/101/111/112/113 durations. Timings stay untouched by #27 — the fix is
        // transmission only, so this also guards against a "fix the clock by editing timings".
        Assert.Equal(3.0, AlliesFlowParams.WarmUp.TotalSeconds, precision: 6);
        Assert.Equal(10.0, AlliesFlowParams.BuyPhase.TotalSeconds, precision: 6);
        Assert.Equal(6.0, AlliesFlowParams.RoundEndPause.TotalSeconds, precision: 6);
        Assert.Equal(5.0, AlliesFlowParams.HalfTimeIntro.TotalSeconds, precision: 6);
        Assert.Equal(1.0, AlliesFlowParams.HalfTimeSwapHold.TotalSeconds, precision: 6);
        Assert.Equal(7.0, AlliesFlowParams.HalfTimeTransition.TotalSeconds, precision: 6);
    }

    [Fact]
    public void ClientCombatCountdown_IsTheGoldHundredTen_WhileHostDeadlineStays109()
    {
        // Gold C2=31 Duration = 110.0 (client starts at 1:50); the host's own timeout is 109s.
        // #27 is transmission-only, so the two must stay deliberately separate.
        Assert.Equal(110.0, AlliesFlowParams.ClientCombatCountdownSeconds, precision: 6);
        Assert.Equal(TimeSpan.FromSeconds(109), AlliesFlowParams.RoundDuration);
    }

    [Fact]
    public void EveryPhaseDurationKey_IsTheWireNameFromGold()
    {
        Assert.Equal("Duration", MatchRoomPropKeys.Duration);
    }

    /// <summary>
    /// Issue #27 — gold 2.07 (802 prop packets, 0 exceptions) carries <c>Duration</c> as the
    /// <b>second</b> key, right after <c>Time</c>, in every C2 bag, and C2=101 walks
    /// Time, Duration, CtScore, TrCoLosses, CtCoLosses, WinTeam, C2. Asserted on the built bag so
    /// a future edit cannot reorder the anchor/countdown pair the client relies on.
    /// </summary>
    [Fact]
    public void AlliesRoundEndBag_KeepsGoldKeyOrderAndBothScores()
    {
        var props = GameMatchHost.BuildAlliesRoundEndRoomProps(
            nowSec: 10,
            winner: MatchTeam.Tr,
            scoreTr: 2,
            scoreCt: 1,
            coLossesTr: 0,
            coLossesCt: 1,
            GameMatchHost.BuildWinTeamProps(MatchTeam.Tr, mvpNr: 4, mvpCode: 3));

        Assert.Equal(
            new[]
            {
                MatchRoomPropKeys.Time,
                MatchRoomPropKeys.Duration,
                MatchRoomPropKeys.CtScore,
                MatchRoomPropKeys.TrScore,
                MatchRoomPropKeys.CtCoLosses,
                MatchRoomPropKeys.TrCoLosses,
                MatchRoomPropKeys.WinTeam,
                MatchRoomPropKeys.C2,
            },
            props.Select(p => p.Key).ToArray());

        Assert.Equal(2, Single(props, MatchRoomPropKeys.TrScore).Int);
        Assert.Equal(1, Single(props, MatchRoomPropKeys.CtScore).Int);
    }

    [Fact]
    public void AlliesRoundEndBag_CtWinAlsoCarriesBothScores()
    {
        var props = GameMatchHost.BuildAlliesRoundEndRoomProps(
            nowSec: 20,
            winner: MatchTeam.Ct,
            scoreTr: 1,
            scoreCt: 3,
            coLossesTr: 1,
            coLossesCt: 0,
            GameMatchHost.BuildWinTeamProps(MatchTeam.Ct, mvpNr: 2, mvpCode: 1));

        Assert.Equal(1, Single(props, MatchRoomPropKeys.TrScore).Int);
        Assert.Equal(3, Single(props, MatchRoomPropKeys.CtScore).Int);
        Assert.Equal(6.0, Single(props, MatchRoomPropKeys.Duration).Double, precision: 6);
        Assert.Equal(20.0, Single(props, MatchRoomPropKeys.Time).Double, precision: 6);
    }

    private static LobbyVariant Single(
        IReadOnlyList<(string Key, LobbyVariant Value)> props, string key)
    {
        var matches = props.Where(p => p.Key == key).ToArray();
        Assert.Single(matches);
        return matches[0].Value;
    }
}

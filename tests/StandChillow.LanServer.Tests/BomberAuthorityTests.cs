using StandChillow.LanServer.Net;
using StandChillow.LanServer.Net.Match;
using Xunit;

namespace StandChillow.LanServer.Tests;

/// <summary>
/// Issue #29 — gold 2.07 (<c>captures/captures.zip</c>) publishes <c>bomberId</c> exactly once per
/// round, inside the C2=22 PreStart bag: 13 packets for 13 rounds, never in any other C2. Extra
/// lone <c>bomberId</c> bags made every client hand out a second C4 ("several bombers, but only
/// one of them can plant").
/// </summary>
public sealed class BomberAuthorityTests
{
    [Fact]
    public void BomberIdWireName_IsTheGoldKey()
    {
        Assert.Equal("bomberId", MatchRoomPropKeys.BomberId);
    }

    [Fact]
    public void RoundEndBag_NeverCarriesBomberId()
    {
        var props = GameMatchHost.BuildAlliesRoundEndRoomProps(
            nowSec: 10,
            winner: MatchTeam.Tr,
            scoreTr: 1,
            scoreCt: 0,
            coLossesTr: 0,
            coLossesCt: 1,
            GameMatchHost.BuildWinTeamProps(MatchTeam.Tr, mvpNr: 1, mvpCode: 2));

        Assert.DoesNotContain(props, p => p.Key == MatchRoomPropKeys.BomberId);
    }

    /// <summary>
    /// Guard against regressing #29: outside the round-start bag no code path may publish a
    /// standalone <c>bomberId</c> room prop. Scans the server sources, so a future change that
    /// re-adds a mid-round bag fails CI instead of shipping a second C4 to every client.
    /// </summary>
    [Fact]
    public void NoStandaloneBomberIdRoomPropIsPublished()
    {
        var offenders = new List<string>();
        foreach (var file in Directory.EnumerateFiles(
                     ServerSourceRoot(), "*.cs", SearchOption.AllDirectories))
        {
            var lines = File.ReadAllLines(file);
            for (var i = 0; i < lines.Length; i++)
            {
                // Only room-prop tuple entries count: `(MatchRoomPropKeys.BomberId, …)`.
                if (!lines[i].Contains("(MatchRoomPropKeys.BomberId,", StringComparison.Ordinal))
                    continue;

                var window = string.Join('\n', lines.Skip(Math.Max(0, i - 25)).Take(50));
                if (!window.Contains("MatchC2States.WarmupWillFinish", StringComparison.Ordinal))
                    offenders.Add($"{Path.GetFileName(file)}:{i + 1}");
            }
        }

        Assert.True(
            offenders.Count == 0,
            "bomberId must only be published in the C2=22 PreStart bag (#29). Offenders: " +
            string.Join(", ", offenders));
    }

    private static string ServerSourceRoot()
    {
        // Tests run from tests/<proj>/bin/<cfg>/<tfm>; walk up to the repo root.
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "server")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return Path.Combine(dir!.FullName, "server");
    }
}

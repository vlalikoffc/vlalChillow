using StandChillow.LanServer.Bans;
using Xunit;

namespace StandChillow.LanServer.Tests;

/// <summary>
/// Срок бана: границы из ТЗ (10 минут … 14 дней) и показ остатка игроку.
/// </summary>
public sealed class BanDurationTests
{
    [Theory]
    [InlineData("10m", 10)]
    [InlineData("30m", 30)]
    [InlineData("6h", 360)]
    [InlineData("3d", 4320)]
    [InlineData("14d", 20160)]
    [InlineData("1d12h", 2160)]
    [InlineData("336h", 20160)]
    [InlineData("90", 90)]
    [InlineData("30м", 30)]
    [InlineData("6ч", 360)]
    [InlineData("3д", 4320)]
    [InlineData("30 мин", 30)]
    [InlineData("2 недели", 20160)]
    public void AcceptsFormsInsideTheRange(string text, int expectedMinutes)
    {
        Assert.True(
            BanDuration.TryParse(text, out var duration, out var error),
            $"'{text}' должен разбираться: {error}");
        Assert.Equal(expectedMinutes, (int)Math.Round(duration.TotalMinutes));
    }

    [Theory]
    [InlineData("9m")]      // меньше 10 минут
    [InlineData("5s")]
    [InlineData("15d")]     // больше 14 дней
    [InlineData("perm")]    // бессрочных нет
    [InlineData("навсегда")]
    [InlineData("abc")]
    [InlineData("30x")]
    [InlineData("")]
    [InlineData("  ")]
    public void RejectsEverythingOutsideTheRange(string text)
    {
        Assert.False(BanDuration.TryParse(text, out _, out var error));
        Assert.False(string.IsNullOrWhiteSpace(error));
    }

    [Fact]
    public void CustomBoundsFromConfigAreHonoured()
    {
        Assert.True(BanDuration.TryParse(
            "2h", TimeSpan.FromMinutes(30), TimeSpan.FromDays(7), out var duration, out _));
        Assert.Equal(TimeSpan.FromHours(2), duration);

        Assert.False(BanDuration.TryParse(
            "20m", TimeSpan.FromMinutes(30), TimeSpan.FromDays(7), out _, out _));
        Assert.False(BanDuration.TryParse(
            "8d", TimeSpan.FromMinutes(30), TimeSpan.FromDays(7), out _, out _));
    }

    [Theory]
    [InlineData(0, 0, 0, 0, "0д 0ч 0м 00с")]
    [InlineData(0, 0, 17, 43, "0д 0ч 17м 43с")]
    [InlineData(1, 2, 3, 4, "1д 2ч 3м 04с")]
    [InlineData(13, 23, 59, 59, "13д 23ч 59м 59с")]
    public void FormatRemainingShowsDaysHoursMinutesSeconds(
        int days, int hours, int minutes, int seconds, string expected)
    {
        var remaining = new TimeSpan(days, hours, minutes, seconds);
        Assert.Equal(expected, BanDuration.FormatRemaining(remaining));
    }

    [Fact]
    public void FormatRemainingRoundsUpSoActiveBanNeverShowsZeroSeconds()
    {
        Assert.Equal("0д 0ч 0м 01с", BanDuration.FormatRemaining(TimeSpan.FromMilliseconds(120)));
        Assert.Equal("0д 0ч 0м 02с", BanDuration.FormatRemaining(TimeSpan.FromMilliseconds(1500)));
    }

    [Theory]
    [InlineData(30, "30м")]
    [InlineData(90, "1ч 30м")]
    [InlineData(360, "6ч")]
    [InlineData(4320, "3д")]
    [InlineData(20160, "14д")]
    public void FormatShortCompactsLongDurations(int minutes, string expected)
    {
        Assert.Equal(expected, BanDuration.FormatShort(TimeSpan.FromMinutes(minutes)));
    }
}

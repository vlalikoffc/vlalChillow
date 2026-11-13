using StandChillow.LanServer.Bans;
using Xunit;

namespace StandChillow.LanServer.Tests;

/// <summary>Разбор <c>/banid &lt;id&gt; &lt;срок&gt; [причина…]</c>.</summary>
public sealed class BanIdCommandLineTests
{
    [Fact]
    public void ParsesIdDurationAndReason()
    {
        Assert.True(BanIdCommandLine.TryParse(
            "671488 24h лох сасал ахахах", out var id, out var duration, out var reason, out var error));

        Assert.Equal(string.Empty, error);
        Assert.Equal(671488, id);
        Assert.Equal("24h", duration);
        Assert.Equal("лох сасал ахахах", reason);
    }

    [Fact]
    public void ReasonIsOptional()
    {
        Assert.True(BanIdCommandLine.TryParse("671488 30m", out var id, out var duration, out var reason, out _));
        Assert.Equal(671488, id);
        Assert.Equal("30m", duration);
        Assert.Equal(string.Empty, reason);
    }

    [Theory]
    [InlineData("")]
    [InlineData("671488")]
    [InlineData("нечисло 30m причина")]
    [InlineData("0 30m")]              // ниже диапазона (0 = «id нет»)
    [InlineData("100000001 30m")]      // выше диапазона
    [InlineData("-5 30m")]
    public void BadLinesAreRejected(string args)
    {
        Assert.False(BanIdCommandLine.TryParse(args, out _, out _, out _, out var error));
        Assert.False(string.IsNullOrWhiteSpace(error));
    }

    [Theory]
    [InlineData("1 30m", 1)]
    [InlineData("42 10m причина", 42)]
    [InlineData("100000000 14d", 100000000)]
    public void EdgeIdsAreAccepted(string args, int expected)
    {
        Assert.True(BanIdCommandLine.TryParse(args, out var id, out _, out _, out var error), error);
        Assert.Equal(expected, id);
    }

    [Fact]
    public void IdRangeMatchesTheServerIdRange()
    {
        Assert.Equal(MatchPublicId.Min, BanIdCommandLine.MinId);
        Assert.Equal(MatchPublicId.Max, BanIdCommandLine.MaxId);
    }
}

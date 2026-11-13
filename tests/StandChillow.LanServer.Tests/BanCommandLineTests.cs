using StandChillow.LanServer.Bans;
using Xunit;

namespace StandChillow.LanServer.Tests;

/// <summary>Разбор <c>/ban &lt;ник&gt; &lt;срок&gt; [причина…]</c>.</summary>
public sealed class BanCommandLineTests
{
    [Fact]
    public void ParsesNickDurationAndFullReason()
    {
        Assert.True(BanCommandLine.TryParse(
            "Ktulxy 30m софт ебучий", out var nick, out var duration, out var reason, out var error));

        Assert.Equal(string.Empty, error);
        Assert.Equal("Ktulxy", nick);
        Assert.Equal("30m", duration);
        Assert.Equal("софт ебучий", reason);
    }

    [Fact]
    public void ReasonKeepsCommasAndQuotes()
    {
        Assert.True(BanCommandLine.TryParse(
            "n1Coutless 6h удали чит, а то забаню", out _, out _, out var reason, out _));
        Assert.Equal("удали чит, а то забаню", reason);
    }

    [Fact]
    public void QuotedNickMayContainSpaces()
    {
        Assert.True(BanCommandLine.TryParse(
            "\"влал chillow\" 3d причина", out var nick, out var duration, out var reason, out _));

        Assert.Equal("влал chillow", nick);
        Assert.Equal("3d", duration);
        Assert.Equal("причина", reason);
    }

    [Fact]
    public void ReasonIsOptional()
    {
        Assert.True(BanCommandLine.TryParse("Ktulxy 3d", out var nick, out var duration, out var reason, out _));
        Assert.Equal("Ktulxy", nick);
        Assert.Equal("3d", duration);
        Assert.Equal(string.Empty, reason);
    }

    [Theory]
    [InlineData("")]
    [InlineData("Ktulxy")]
    [InlineData("\"не закрыт 30m причина")]
    [InlineData("Ktulxy  ")]
    public void BadLinesAreRejectedWithAnError(string args)
    {
        Assert.False(BanCommandLine.TryParse(args, out _, out _, out _, out var error));
        Assert.False(string.IsNullOrWhiteSpace(error));
    }

    [Fact]
    public void AbsurdlyLongNickIsRejected()
    {
        var line = new string('x', 65) + " 30m причина";
        Assert.False(BanCommandLine.TryParse(line, out _, out _, out _, out var error));
        Assert.Contains("64", error);
    }
}

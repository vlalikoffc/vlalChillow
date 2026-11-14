using StandChillow.LanServer.Net.Match.Kernel;
using Xunit;

namespace StandChillow.LanServer.Tests;

/// <summary>
/// PR 4 плана <c>docs/ROOMS_AND_WEB_PLAN.md</c>: <c>/join &lt;режим&gt; #&lt;номер&gt;</c>
/// и текст «комната не найдена». Парсер чистый — сокеты не нужны.
/// </summary>
public sealed class JoinCommandParserTests
{
    [Theory]
    [InlineData("2v2 #3", "2v2", 3)]
    [InlineData("tdm #1", "tdm", 1)]
    [InlineData("duel #42", "duel", 42)]
    [InlineData("  esc   #7  ", "esc", 7)]
    [InlineData("ranked defuse #12", "ranked defuse", 12)]
    [InlineData("2v2#8", "2v2", 8)]
    [InlineData("2v2 # 3", "2v2", 3)] // пробел после решётки — обычная опечатка
    [InlineData("2V2 #3", "2V2", 3)]
    public void ParsesModeAndNumber(string args, string mode, int number)
    {
        var p = JoinCommandParser.Parse(args);
        Assert.True(p.Ok);
        Assert.Equal(mode, p.ModeToken);
        Assert.Equal(number, p.RoomNumber);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    [InlineData("2v2")]
    [InlineData("#3")]
    [InlineData("2v2 #")]
    [InlineData("2v2 #0")]
    [InlineData("2v2 #-1")]
    [InlineData("2v2 #3a")]
    [InlineData("2v2 3")]
    [InlineData("#")]
    public void RejectsMalformedInput(string? args)
    {
        Assert.False(JoinCommandParser.Parse(args).Ok);
    }

    [Fact]
    public void RejectsAbsurdlyLargeNumber()
    {
        Assert.False(JoinCommandParser.Parse($"2v2 #{JoinCommandParser.MaxRoomNumber + 1}").Ok);
        Assert.True(JoinCommandParser.Parse($"2v2 #{JoinCommandParser.MaxRoomNumber}").Ok);
    }

    [Fact]
    public void RejectsOverflowWithoutThrowing()
    {
        Assert.False(JoinCommandParser.Parse("2v2 #99999999999999999999").Ok);
    }

    [Fact]
    public void NotFoundTextMatchesSpecExactly()
    {
        var text = JoinCommandParser.FormatNotFound("Ранкед 2х2", 3, "example.com");

        Assert.Equal(
            "Комната «Ранкед 2х2 * 3» не найдена. Создать можно здесь: example.com",
            text);
    }

    [Fact]
    public void ModeDisplayNameIsQuotedVerbatim()
    {
        // Спека: «Комната «<режим> * <номер>» не найдена» — режим как его зовёт каталог.
        var text = JoinCommandParser.FormatNotFound("Duel", 7, "10.0.0.5");
        Assert.Contains("«Duel * 7»", text);
        Assert.Contains("10.0.0.5", text);
    }
}

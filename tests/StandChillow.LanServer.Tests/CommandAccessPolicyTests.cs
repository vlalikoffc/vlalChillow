using StandChillow.LanServer.Config;
using StandChillow.LanServer.Net;
using Xunit;

namespace StandChillow.LanServer.Tests;

/// <summary>
/// Матрица прав на команды из чата.
/// </summary>
/// <remarks>
/// Требование: <c>/set</c> (любая подкоманда) и <c>/end</c> — админские; запуск катки
/// (<c>/mode</c>, <c>/map</c>, <c>/start</c>, <c>/play</c>) — доступен всем. Тесты держат
/// это поведение и не дают новому правилу незаметно закрыть запуск матча.
/// </remarks>
public sealed class CommandAccessPolicyTests
{
    private static ServerConfig Default()
    {
        var cfg = new ServerConfig();
        cfg.Normalize();
        return cfg;
    }

    private static bool Allowed(ServerConfig cfg, string nick, string message, out string reason) =>
        CommandAccessPolicy.IsAllowed(cfg, nick, message, out reason);

    [Theory]
    [InlineData("/mode")]
    [InlineData("/mode Escalation")]
    [InlineData("/map")]
    [InlineData("/map Sandstone")]
    [InlineData("/start")]
    [InlineData("/startmatch")]
    [InlineData("/старт")]
    [InlineData("/play")]
    [InlineData("/игра")]
    [InlineData("start")]
    [InlineData("play")]
    public void StartingTheMatch_IsAllowedForEveryoneByDefault(string message)
    {
        var cfg = Default();

        Assert.True(Allowed(cfg, "случайный игрок", message, out var reason),
            $"'{message}' должен быть доступен без админки");
        Assert.Equal(string.Empty, reason);
    }

    [Theory]
    [InlineData("/set start")]
    [InlineData("set start")]
    public void SetStart_StartsTheMatchAndIsAllowedForEveryone(string message)
    {
        var cfg = Default();

        // anyoneCanStart=true по умолчанию: /set start — это запуск катки, а не настройка.
        Assert.True(Allowed(cfg, "случайный игрок", message, out _));
    }

    [Theory]
    [InlineData("/set round 16")]
    [InlineData("/set wins 9")]
    [InlineData("/set team ct")]
    [InlineData("/set status")]
    [InlineData("/set end")]
    [InlineData("/set")]
    public void EveryOtherSetSubcommand_NeedsAdmin(string message)
    {
        var cfg = Default();

        Assert.False(Allowed(cfg, "случайный игрок", message, out var reason),
            $"'{message}' меняет параметры хоста — должен требовать админа");
        Assert.Contains("set", reason);
    }

    [Theory]
    [InlineData("/end")]
    [InlineData("/stopmatch")]
    [InlineData("/стопматч")]
    public void EndingTheMatch_NeedsAdmin(string message)
    {
        var cfg = Default();

        Assert.False(Allowed(cfg, "случайный игрок", message, out _));
    }

    [Theory]
    [InlineData("/help")]
    [InlineData("/?")]
    [InlineData("/команды")]
    [InlineData("/ready")]
    [InlineData("/unready")]
    [InlineData("/готов")]
    [InlineData("/нг")]
    public void PublicCommands_AreAlwaysAllowed(string message)
    {
        Assert.True(Allowed(Default(), "кто угодно", message, out _));
    }

    [Theory]
    [InlineData("/set round 16")]
    [InlineData("/end")]
    public void AdminCommands_AreAllowedForConfiguredAdmins(string message)
    {
        var cfg = Default();
        cfg.Admin.Nicknames.Add("влал");

        Assert.True(Allowed(cfg, "влал", message, out _));
        // И регистр/невидимые символы не мешают.
        Assert.True(Allowed(cfg, "ВЛАЛ", message, out _));
    }

    [Fact]
    public void WhenNobodyCanStart_OnlyAdminsCanLaunchTheMatch()
    {
        var cfg = Default();
        cfg.Admin.Nicknames.Add("влал");
        cfg.Admin.AnyoneCanStart = false;

        Assert.False(Allowed(cfg, "случайный игрок", "/start", out _));
        Assert.False(Allowed(cfg, "случайный игрок", "/set start", out _));
        Assert.True(Allowed(cfg, "влал", "/start", out _));
        Assert.True(Allowed(cfg, "влал", "/set start", out _));
    }

    [Fact]
    public void ModeAndMapStayOpenEvenWhenSetIsAdminOnly()
    {
        // Смысл настройки: /set меняет параметры, но выбирать режим и карту может любой.
        var cfg = Default();
        cfg.Admin.AnyoneCanStart = false;

        Assert.True(Allowed(cfg, "игрок", "/mode Duel", out _));
        Assert.True(Allowed(cfg, "игрок", "/map Sandstone", out _));
        Assert.False(Allowed(cfg, "игрок", "/set round 4", out _));
    }

    [Fact]
    public void CustomAdminOnlyList_IsRespectedForOtherCommands()
    {
        var cfg = Default();
        cfg.Admin.AdminOnlyCommands.Add("play");

        Assert.False(Allowed(cfg, "игрок", "/play", out _));
        Assert.True(Allowed(cfg, "игрок", "/mode", out _));
    }

    [Fact]
    public void Parse_RecognisesSetStartAsMatchStart()
    {
        var parsed = CommandAccessPolicy.Parse("/set start");

        Assert.Equal("set", parsed.Command);
        Assert.True(parsed.StartsMatch);
    }

    [Fact]
    public void Parse_TreatsSetRoundAsAPlainSet()
    {
        var parsed = CommandAccessPolicy.Parse("/set round 16");

        Assert.Equal("set", parsed.Command);
        Assert.Equal("round 16", parsed.Args);
        Assert.False(parsed.StartsMatch);
    }

    [Fact]
    public void DenialReason_AlwaysNamesTheCommandAndTheNick()
    {
        Assert.False(Allowed(Default(), "влал3", "/set round 8", out var reason));

        // Не «молча проигнорировали»: в причине есть и команда, и ник игрока.
        Assert.Contains("/set", reason);
        Assert.Contains("влал3", reason);
    }
}

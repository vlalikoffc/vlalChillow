using StandChillow.LanServer.Net;
using Xunit;

namespace StandChillow.LanServer.Tests;

/// <summary>
/// Что сервер говорит игроку про команды.
/// </summary>
/// <remarks>
/// Требование: при входе в лобби инструкция больше не присылается — человек должен
/// видеть только «кто он такой», а список команд получать по <c>/help</c>. И список
/// этот короткий: комнаты создаются на сайте, поэтому <c>/mode</c>, <c>/map</c>,
/// <c>/start</c>, <c>/play</c> и <c>/set</c> игроку больше не нужны.
/// Тесты держат обе границы: памятка не разрастается обратно и команды, которые
/// действительно нужны, не пропадают совсем (иначе <c>/join</c> станет неоткуда узнать).
/// </remarks>
public sealed class CommandHelpTests
{
    private static readonly string[] NotForPlayers =
    {
        "/mode", "/map", "/start", "/play", "/set", "/chattest", "/auth",
    };

    [Fact]
    public void PlayerHelpDoesNotMentionSetupCommands()
    {
        var help = GameNetHost.FormatPlayerHelp();

        foreach (var command in NotForPlayers)
            Assert.DoesNotContain(command, help);
    }

    [Fact]
    public void PlayerHelpStillExplainsHowToGetIn()
    {
        var help = GameNetHost.FormatPlayerHelp();

        // Без этих строк игрок не узнает ни про вход в комнату, ни про сам /help.
        Assert.Contains("/join", help);
        Assert.Contains("/help", help);
        Assert.Contains("/ready", help);
        Assert.Contains("/whoami", help);
    }

    [Fact]
    public void PlayerHelpPointsToTheSiteForRoomCreation()
    {
        // Главная причина, по которой команды создания и запуска больше не нужны:
        // комнату поднимают кнопкой на сайте, и игрок должен об этом знать.
        var help = GameNetHost.FormatPlayerHelp();

        Assert.Contains("сайт", help);
    }

    [Fact]
    public void PlayerHelpIsShort()
    {
        // Смысл правки — не заваливать чат. Полный список занимал 18 строк.
        var lines = GameNetHost.FormatPlayerHelp()
            .Split('\n', StringSplitOptions.RemoveEmptyEntries);

        Assert.True(lines.Length <= 6, $"строк в памятке: {lines.Length}");
    }

    [Fact]
    public void ConsoleHelpKeepsWhatStillExists()
    {
        // Оператор в консоли не должен потерять ни одной живой команды из-за того,
        // что игрокам мы список сократили.
        var help = GameNetHost.FormatConsoleHelp();

        foreach (var command in new[] { "/mode", "/map", "/set", "/join", "/end" })
            Assert.Contains(command, help);

        Assert.Contains("/ban", help);
        Assert.Contains("/ids", help);
        Assert.Contains("/chattest", help);
    }

    [Fact]
    public void NoHelpMentionsTheRemovedStartCommands()
    {
        // /play и /start убраны совсем, а не спрятаны: их не должно быть ни в
        // памятке игрока, ни в консольной. Иначе кто-то наберёт и решит, что сломалось.
        foreach (var help in new[]
                 {
                     GameNetHost.FormatPlayerHelp(),
                     GameNetHost.FormatConsoleHelp(),
                 })
        {
            Assert.DoesNotContain("/play", help);
            Assert.DoesNotContain("/start", help);
            Assert.DoesNotContain("startmatch", help);
        }
    }

    [Fact]
    public void OldNameStillResolvesToFullHelp()
    {
        // Совместимость: прежнее имя оставлено, и это именно полная памятка.
        Assert.Equal(GameNetHost.FormatConsoleHelp(), GameNetHost.FormatCommandHelp());
    }
}

using StandChillow.LanServer.Net;
using Xunit;

namespace StandChillow.LanServer.Tests;

/// <summary>
/// Команды из чата катки обязаны исполняться хостом, а админские — проверять админ-права.
/// </summary>
/// <remarks>
/// <para>
/// Отчёт: «сделай чтоб он их исполнял, а на админские смотрел админ права». Цепочка длинная и
/// рвётся молча: ChatManager (id=7) → <c>OnMatchChatSlashCommand</c> → резолв игрока →
/// <c>CommandAccessPolicy</c> → диспетчер команд → ответ в матч-чат. Если убрать любое звено,
/// в журнале не останется ни ошибки, ни отказа — игрок просто увидит тишину. Поэтому цепочка
/// зафиксирована тестом по исходникам (так же сделано в <see cref="MatchChatDeliveryTests"/>).
/// </para>
/// <para>
/// Права — не «ник в <c>admin.nicknames</c>», а ник плюс код этого запуска, предъявленный
/// адресом: ник присылает клиент, и <c>/chattest</c> доказывает, что кадр ChatManager
/// подписывается чужим актором. Значит команда обязана исполняться от имени отправителя
/// (<c>st.ActorNr</c>), а не от имени байта <c>rpc</c> из провода.
/// </para>
/// </remarks>
public sealed class MatchChatCommandTests
{
    [Fact]
    public void ChatManagerSlash_IsHandedToTheHost_AndNotRelayed()
    {
        var worldObjects = ReadServer("Net", "Match", "Host", "GameMatchHost.WorldObjects.cs");

        // Slash уходит в хост ником отправителя и его адресом (IP — запасной путь резолва).
        Assert.Contains(
            "OnMatchChatSlashCommand?.Invoke(chatActorName, peer.Address, chatText)",
            worldObjects,
            StringComparison.Ordinal);

        // Текст команды не веерится остальным: это не сообщение, а приватный диалог с Server.
        Assert.Contains("dropRelay = true;", worldObjects, StringComparison.Ordinal);

        // Ник — из комнаты по номеру актора отправителя, а не по байту «rpc» из кадра.
        Assert.Contains(
            "var chatActorName = ResolveActorName(st.Room, st.ActorNr);",
            worldObjects,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "OnMatchChatSlashCommand?.Invoke(ResolveActorName(st.Room, parsed.RpcId)",
            worldObjects,
            StringComparison.Ordinal);
    }

    [Fact]
    public void HostExecutesTheCommand_ThroughTheSameDispatcherAsLobby()
    {
        var host = ReadServer("Net", "GameNetHost.cs");

        Assert.Contains(
            "_match.OnMatchChatSlashCommand = HandleMatchChatSlashCommand;",
            host,
            StringComparison.Ordinal);

        // Тот же вход, что и у лобби-чата: команды катки не живут отдельным списком.
        Assert.Contains("TryHandleSlashCommand(player, message)", host, StringComparison.Ordinal);

        // Исполнение видно в журнале даже с выключенным debug-chat: иначе «выполнилась ли
        // команда» снова придётся угадывать по косвенным признакам.
        Assert.Contains(
            "(ChatManager command, silent; ",
            host,
            StringComparison.Ordinal);
        Assert.Contains(
            "команда из катки обработана хостом",
            host,
            StringComparison.Ordinal);

        // Нераспознанная команда не молчит — автору уходит подсказка.
        Assert.Contains("FormatUnknownCommandHelp(message)", host, StringComparison.Ordinal);
    }

    [Fact]
    public void AdminGate_ChecksTheRunCode_NotJustTheNick()
    {
        var commands = ReadServer("Net", "GameNetHost.Commands.cs");

        Assert.Contains("CommandAccessPolicy.IsAllowed(", commands, StringComparison.Ordinal);
        Assert.Contains(
            "nick => IsAdminAuthorized(nick, player.Peer.Address)",
            commands,
            StringComparison.Ordinal);
    }

    /// <summary>
    /// Ответ на команду ждёт адресата дольше зеркала ростера: игрок выпадает из катки и
    /// возвращается. latest-3.log — матч-пир отвалился (<c>RemoteConnectionClose</c>) до
    /// <c>/chattest</c>, и все пять ответов истекли за минуту ровно в это окно.
    /// </summary>
    [Fact]
    public void AddressedReplyOutlivesTheRosterMirror()
    {
        Assert.Equal(TimeSpan.FromSeconds(60), GameNetHost.ReplyTtl(broadcast: true));
        Assert.Equal(TimeSpan.FromSeconds(300), GameNetHost.ReplyTtl(broadcast: false));
    }

    /// <summary>
    /// <c>/chattest</c> обязан назвать аудиторию: проба, ушедшая в комнату без заказчика, не
    /// отвечает ни на один вопрос (latest-3.log — буквы увидели двое других игроков, а
    /// заказчик не увидел ничего).
    /// </summary>
    [Fact]
    public void ChatTestNamesItsAudience()
    {
        var commands = ReadServer("Net", "GameNetHost.Commands.cs");

        Assert.Contains("DescribeChatAudience(", commands, StringComparison.Ordinal);
        Assert.Contains("тебя среди них нет", commands, StringComparison.Ordinal);
    }

    private static string ReadServer(params string[] parts)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "server")))
            dir = dir.Parent;
        Assert.NotNull(dir);

        var path = Path.Combine(new[] { dir!.FullName, "server" }.Concat(parts).ToArray());
        Assert.True(File.Exists(path), $"нет исходника {path}");
        return File.ReadAllText(path);
    }
}

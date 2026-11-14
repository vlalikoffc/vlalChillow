using StandChillow.LanServer.Config;

namespace StandChillow.LanServer.Net;

/// <summary>
/// Кому разрешена команда из чата. Чистая функция — без сокетов и без состояния,
/// чтобы матрицу прав можно было проверить тестом, а не «на глазок» в игре.
/// </summary>
/// <remarks>
/// Порядок решения: справка/готовность (всегда всем) → запуск матча
/// (<c>admin.anyoneCanStart</c>) → команда не в <c>adminOnlyCommands</c> (всем) →
/// админ по нику. Всё остальное — отказ с причиной, которую вызывающий обязан
/// показать игроку и записать в лог.
/// </remarks>
public static class CommandAccessPolicy
{
    /// <summary>Разобранная команда из строки чата.</summary>
    public readonly record struct Parsed(string Command, string Args, bool StartsMatch);

    /// <summary>Разбирает <c>/set start</c>, <c>set round 16</c>, <c>start</c>.</summary>
    public static Parsed Parse(string message)
    {
        var body = message.Trim();
        if (body.StartsWith('/'))
            body = body[1..].Trim();

        if (body.Length == 0)
            return new Parsed(string.Empty, string.Empty, false);

        var space = body.IndexOf(' ');
        var command = space < 0 ? body : body[..space].Trim();
        var args = space < 0 ? string.Empty : body[(space + 1)..].Trim();

        // Запуск матча: /start и его синонимы, а также /set start.
        var startsMatch =
            IsStartAlias(command)
            || (command.Equals("set", StringComparison.OrdinalIgnoreCase)
                && args.Equals("start", StringComparison.OrdinalIgnoreCase));

        return new Parsed(command, args, startsMatch);
    }

    /// <summary>Команды, которые никогда не требуют прав.</summary>
    public static bool IsPublic(Parsed parsed) =>
        parsed.Command.Equals("help", StringComparison.OrdinalIgnoreCase)
        || parsed.Command.Equals("?", StringComparison.OrdinalIgnoreCase)
        || parsed.Command.Equals("команды", StringComparison.OrdinalIgnoreCase)
        || parsed.Command.Equals("ready", StringComparison.OrdinalIgnoreCase)
        || parsed.Command.Equals("unready", StringComparison.OrdinalIgnoreCase)
        || parsed.Command.Equals("готов", StringComparison.OrdinalIgnoreCase)
        || parsed.Command.Equals("неготов", StringComparison.OrdinalIgnoreCase)
        || parsed.Command.Equals("г", StringComparison.OrdinalIgnoreCase)
        || parsed.Command.Equals("нг", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Можно ли игроку выполнить команду. <paramref name="denyReason"/> заполняется только
    /// при отказе — «молча проигнорировать» здесь невозможно by design.
    /// </summary>
    public static bool IsAllowed(
        ServerConfig config,
        string? playerNick,
        string message,
        out string denyReason)
    {
        denyReason = string.Empty;

        var parsed = Parse(message);
        if (parsed.Command.Length == 0)
            return true;

        if (IsPublic(parsed))
            return true;

        // Запуск матча открыт всем — это и есть «неадминские /mode /map /start /play».
        if (parsed.StartsMatch && config.Admin.AnyoneCanStart)
            return true;

        // Баны — отдельный гейт: по умолчанию только админ, независимо от
        // admin.adminOnlyCommands. Разбан randomplayer'ом — это не «мелкая опция».
        if (config.Admin.Ban.AdminOnly && IsBanCommand(parsed.Command))
        {
            if (config.IsAdmin(playerNick))
                return true;

            denyReason = AdminDenied(config, parsed.Command, playerNick);
            return false;
        }

        if (!config.IsAdminOnly(parsed.Command) && !IsAdminOnlyAlias(config, parsed.Command))
            return true;

        if (config.IsAdmin(playerNick))
            return true;

        denyReason = AdminDenied(config, parsed.Command, playerNick);
        return false;
    }

    /// <summary>
    /// Отказ с ответом на вопрос «почему я не админ»: сервер показывает ник, как он его
    /// видит, и список админов из конфига (частый случай — список пуст).
    /// </summary>
    private static string AdminDenied(ServerConfig config, string command, string? playerNick)
    {
        var admins = config.Admin.Nicknames.Count == 0
            ? "в config.json admin.nicknames пусто — админ-команд ни у кого нет"
            : $"админы: {string.Join(", ", config.Admin.Nicknames)}";
        return $"команда /{command} только для админов (сервер видит твой ник как " +
               $"'{playerNick}'; {admins})";
    }

    /// <summary>
    /// Алиасы админских команд. <c>stopmatch</c>/<c>стопматч</c> делают то же, что
    /// <c>end</c>, и обязаны требовать тех же прав — иначе гейт обходится синонимом.
    /// </summary>
    private static bool IsAdminOnlyAlias(ServerConfig config, string command)
    {
        if (config.IsAdminOnly("end")
            && (command.Equals("stopmatch", StringComparison.OrdinalIgnoreCase)
                || command.Equals("стопматч", StringComparison.OrdinalIgnoreCase)))
            return true;

        return false;
    }

    private static bool IsStartAlias(string command) =>
        command.Equals("start", StringComparison.OrdinalIgnoreCase)
        || command.Equals("startmatch", StringComparison.OrdinalIgnoreCase)
        || command.Equals("старт", StringComparison.OrdinalIgnoreCase);

    /// <summary>Команды банов: <c>/ban</c>, <c>/unban</c>, <c>/bans</c> и русские формы.</summary>
    public static bool IsBanCommand(string? command) =>
        command is not null
        && (command.Equals("ban", StringComparison.OrdinalIgnoreCase)
            || command.Equals("бан", StringComparison.OrdinalIgnoreCase)
            || command.Equals("banid", StringComparison.OrdinalIgnoreCase)
            || command.Equals("банид", StringComparison.OrdinalIgnoreCase)
            || command.Equals("unban", StringComparison.OrdinalIgnoreCase)
            || command.Equals("разбан", StringComparison.OrdinalIgnoreCase)
            || command.Equals("unbanid", StringComparison.OrdinalIgnoreCase)
            || command.Equals("bans", StringComparison.OrdinalIgnoreCase)
            || command.Equals("баны", StringComparison.OrdinalIgnoreCase)
            || command.Equals("ids", StringComparison.OrdinalIgnoreCase)
            || command.Equals("айди", StringComparison.OrdinalIgnoreCase));
}

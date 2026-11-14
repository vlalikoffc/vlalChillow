using StandChillow.LanServer.Config;

namespace StandChillow.LanServer.Net;

/// <summary>
/// Кому разрешена команда из чата. Чистая функция — без сокетов и без состояния,
/// чтобы матрицу прав можно было проверить тестом, а не «на глазок» в игре.
/// </summary>
/// <remarks>
/// <para>
/// Порядок решения: справка/готовность (всегда всем) → запуск матча
/// (<c>admin.anyoneCanStart</c>) → команда не в <c>adminOnlyCommands</c> (всем) →
/// админ. Всё остальное — отказ с причиной, которую вызывающий обязан
/// показать игроку и записать в лог.
/// </para>
/// <para>
/// «Админ» здесь — не ник из конфига, а делегат <c>hasAdminRights</c> у
/// <see cref="IsAllowed"/>: ник плюс код этого запуска, предъявленный адресом (см.
/// <c>AdminAuth</c>). Один и тот же гейт применяется и к лобби-чату (7778), и к командам из
/// матч-чата (ChatManager, 7777) — канал у игрока меняется, а права нет.
/// </para>
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
    /// <param name="hasAdminRights">
    /// Настоящие админ-права написавшего. По умолчанию это ник из <c>admin.nicknames</c>,
    /// но живые каналы (лобби 7778 и ChatManager 7777) обязаны передавать сюда
    /// <c>GameNetHost.IsAdminAuthorized</c> — ник плюс код этого запуска, предъявленный
    /// адресом. Ник присылает клиент: без делегата любой, кто назвал себя «влал», получил бы
    /// <c>/ban</c> и <c>/set</c>, хотя <c>/whoami</c> в тот же момент честно отвечал бы
    /// «ник админский, код не введён».
    /// </param>
    public static bool IsAllowed(
        ServerConfig config,
        string? playerNick,
        string message,
        out string denyReason,
        Func<string?, bool>? hasAdminRights = null)
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

        // Ник в списке и права — разные вещи: права даёт адрес, предъявивший код запуска.
        var adminNick = config.IsAdmin(playerNick);
        var admin = hasAdminRights is null ? adminNick : hasAdminRights(playerNick);

        // Баны — отдельный гейт: по умолчанию только админ, независимо от
        // admin.adminOnlyCommands. Разбан randomplayer'ом — это не «мелкая опция».
        if (config.Admin.Ban.AdminOnly && IsBanCommand(parsed.Command))
        {
            if (admin)
                return true;

            denyReason = AdminDenied(config, parsed.Command, playerNick, adminNick);
            return false;
        }

        // Диагностика матч-чата шлёт кадры от имени чужого актора — тоже только админ,
        // и тоже независимо от admin.adminOnlyCommands (иначе гейт снимается правкой конфига).
        if (IsChatTestCommand(parsed.Command))
        {
            if (admin)
                return true;

            denyReason = AdminDenied(config, parsed.Command, playerNick, adminNick);
            return false;
        }

        if (!config.IsAdminOnly(parsed.Command) && !IsAdminOnlyAlias(config, parsed.Command))
            return true;

        if (admin)
            return true;

        denyReason = AdminDenied(config, parsed.Command, playerNick, adminNick);
        return false;
    }

    /// <summary>
    /// Отказ с ответом на вопрос «почему я не админ»: сервер показывает ник, как он его
    /// видит, и список админов из конфига (частый случай — список пуст).
    /// </summary>
    /// <remarks>
    /// Ник админский, а прав нет — это отдельный случай (код запуска не предъявлен), и
    /// молчать о нём нельзя: игрок видит себя в <c>admin.nicknames</c> и решает, что команда
    /// «сломалась». Подсказка — та же, что даёт <c>/whoami</c>.
    /// </remarks>
    private static string AdminDenied(
        ServerConfig config,
        string command,
        string? playerNick,
        bool adminNickWithoutCode)
    {
        if (adminNickWithoutCode)
            return $"команда /{command} только для админов: ник '{playerNick}' в " +
                   "admin.nicknames есть, но этот адрес не предъявил код запуска — " +
                   "прав пока нет (/whoami; код спрашивают при входе в лобби)";

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

    /// <summary>
    /// Диагностика матч-чата (<c>/chattest</c>): шлёт один и тот же текст четырьмя кадрами —
    /// автор (хост/живой игрок) × <c>field</c> (1/2). Все значения из капчур, но кадры
    /// подписаны чужим актором, поэтому команда админская.
    /// </summary>
    public static bool IsChatTestCommand(string? command) =>
        command is not null
        && (command.Equals("chattest", StringComparison.OrdinalIgnoreCase)
            || command.Equals("чаттест", StringComparison.OrdinalIgnoreCase)
            || command.Equals("testchat", StringComparison.OrdinalIgnoreCase));
}

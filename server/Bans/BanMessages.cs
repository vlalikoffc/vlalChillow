namespace StandChillow.LanServer.Bans;

/// <summary>
/// Текст, который видит забаненный и остальные. Отдельный класс — чтобы формулировки
/// проверялись тестом, а не «на глаз» по логам.
/// </summary>
/// <remarks>
/// Это обычные лобби-сообщения от Server (OpChat, sender id 0). Новый опкод не нужен:
/// забаненный видит ровно те же строки, что и приветствие при входе, только адресно.
/// </remarks>
public static class BanMessages
{
    /// <summary>Четыре строки, которые видит забаненный при попытке войти в лобби.</summary>
    public static string[] NoticeLines(BanRecord record, DateTime nowUtc) => new[]
    {
        "Вы были забанены!",
        $"Причина: {DescribeReason(record)}",
        $"Забанил: {DescribeAdmin(record)}",
        CountdownLine(record, nowUtc),
    };

    /// <summary>Обновляемая строка «Разбан через: …».</summary>
    public static string CountdownLine(BanRecord record, DateTime nowUtc) =>
        $"Разбан через: {BanDuration.FormatRemaining(record.Remaining(nowUtc))}";

    /// <summary>Одна строка в общий чат (если admin.ban.notifyEveryone).</summary>
    public static string PublicLine(BanRecord record)
    {
        var reason = string.IsNullOrWhiteSpace(record.Reason)
            ? string.Empty
            : $" ({record.Reason.Trim()})";
        return $"{record.DisplayNick} забанен на {BanDuration.FormatShort(record.Duration)}{reason}";
    }

    /// <summary>
    /// Объявление в общий чат: кто забанил, кого (и его id), на сколько и за что.
    /// </summary>
    /// <remarks>
    /// Пользователь: «чтоб все знали, сервер ваще нихуя не делает» и «после ника добавь ещё
    /// строку айди, чтоб было видно, вдруг разбан нужен». Id стоит сразу после ника: разбан
    /// делается командой <c>/unban &lt;id&gt;</c>, и искать число в <c>/bans</c> потом поздно.
    /// </remarks>
    public static string[] PublicLines(BanRecord record) => new[]
    {
        $"Игрок {DescribeAdmin(record)} забанил {record.DisplayNick}!",
        $"Айди: {IdOf(record)}",
        $"Длительность бана: {BanDuration.FormatShort(record.Duration)}",
        $"Причина: {DescribeReason(record)}",
    };

    /// <summary>
    /// Id для объявления: из записи (там тег того, кого банили), а если запись старая и тега
    /// нет — считается из ника, он детерминирован.
    /// </summary>
    public static int IdOf(BanRecord record) =>
        record.MatchIds.Count > 0 ? record.MatchIds[0] : MatchPublicId.ForNick(record.Nick);

    private static string DescribeReason(BanRecord record) =>
        string.IsNullOrWhiteSpace(record.Reason) ? "не указана" : record.Reason.Trim();

    private static string DescribeAdmin(BanRecord record) =>
        string.IsNullOrWhiteSpace(record.BannedBy) ? "Server" : record.BannedBy.Trim();
}

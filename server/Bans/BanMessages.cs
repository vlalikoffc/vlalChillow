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

    /// <summary>Строка в общий чат (если admin.ban.notifyEveryone).</summary>
    public static string PublicLine(BanRecord record)
    {
        var reason = string.IsNullOrWhiteSpace(record.Reason)
            ? string.Empty
            : $" ({record.Reason.Trim()})";
        return $"{record.DisplayNick} забанен на {BanDuration.FormatShort(record.Duration)}{reason}";
    }

    private static string DescribeReason(BanRecord record) =>
        string.IsNullOrWhiteSpace(record.Reason) ? "не указана" : record.Reason.Trim();

    private static string DescribeAdmin(BanRecord record) =>
        string.IsNullOrWhiteSpace(record.BannedBy) ? "Server" : record.BannedBy.Trim();
}

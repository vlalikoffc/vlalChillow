using System.Globalization;
using System.Text.Json.Serialization;
using StandChillow.LanServer.Config;

namespace StandChillow.LanServer.Bans;

/// <summary>
/// Одна личность в <c>banlist.json</c>: ник(и), IP(и), время бана и причина.
/// </summary>
/// <remarks>
/// <para>
/// Записей ровно столько, сколько людей забанили: алиасы и адреса одного нарушителя
/// дописываются в <b>его</b> запись, а не размножаются на новых. Поэтому 200 банов
/// читаются как 200 личностей, без «склеивания» и без путаницы.
/// </para>
/// <para>
/// Время хранится в unix-миллисекундах (UTC) — это единственный формат, который не
/// поедет, если на сервере сменят часовой пояс или переведут часы на летнее время.
/// Смещение и локальная строка пишутся рядом только для чтения глазами.
/// </para>
/// </remarks>
public sealed class BanRecord
{
    /// <summary>Ник, которого забанил админ (нормализованный).</summary>
    public string Nick { get; set; } = "";

    /// <summary>Другие ники этой же личности (зашёл под новым ником с забаненного IP).</summary>
    public List<string> Aliases { get; set; } = new();

    /// <summary>Все IP этой личности: LAN, Hamachi, VPN, публичный — без разницы.</summary>
    public List<string> Ips { get; set; } = new();

    /// <summary>
    /// Серверные id матча (100…1 000 000), под которыми личность попала в бан.
    /// Нужны только для <c>/unbanid</c>: сам id — селектор, а не ключ бана.
    /// </summary>
    public List<int> MatchIds { get; set; } = new();

    public string Reason { get; set; } = "";
    public string BannedBy { get; set; } = "Server";

    /// <summary>Когда выдан бан (unix ms, UTC) — источник истины для срока.</summary>
    public long CreatedUtcMs { get; set; }

    /// <summary>Когда бан кончается (unix ms, UTC).</summary>
    public long ExpiresUtcMs { get; set; }

    /// <summary>Срок, который выдал админ (для «забанен на 30м»).</summary>
    public int DurationSeconds { get; set; }

    /// <summary>Смещение часового пояса сервера в момент бана (минуты от UTC).</summary>
    public int UtcOffsetMinutes { get; set; }

    /// <summary>Локальное время выдачи бана строкой — только для чтения глазами.</summary>
    public string CreatedLocalIso { get; set; } = "";

    [JsonIgnore]
    public DateTime CreatedUtc => DateTimeOffset.FromUnixTimeMilliseconds(CreatedUtcMs).UtcDateTime;

    [JsonIgnore]
    public DateTime ExpiresUtc => DateTimeOffset.FromUnixTimeMilliseconds(ExpiresUtcMs).UtcDateTime;

    [JsonIgnore]
    public TimeSpan Duration => TimeSpan.FromSeconds(Math.Max(0, DurationSeconds));

    /// <summary>UTC → unix ms без неявных локальных сдвигов.</summary>
    public static long ToUnixMs(DateTime utc)
    {
        var asUtc = utc.Kind == DateTimeKind.Utc
            ? utc
            : DateTime.SpecifyKind(utc, DateTimeKind.Utc);
        return new DateTimeOffset(asUtc).ToUnixTimeMilliseconds();
    }

    /// <summary>Смещение текущего пояса сервера в минутах (для записи в banlist).</summary>
    public static int CurrentUtcOffsetMinutes() =>
        (int)TimeZoneInfo.Local.GetUtcOffset(DateTime.UtcNow).TotalMinutes;

    /// <summary>Локальная строка с смещением, например <c>2026-11-13T15:12:33+03:00</c>.</summary>
    public static string LocalIsoNow() =>
        DateTimeOffset.Now.ToString("yyyy-MM-dd'T'HH:mm:sszzz", CultureInfo.InvariantCulture);

    public bool IsActive(DateTime nowUtc) => ExpiresUtcMs > ToUnixMs(nowUtc);

    public TimeSpan Remaining(DateTime nowUtc)
    {
        var left = ExpiresUtcMs - ToUnixMs(nowUtc);
        return left <= 0 ? TimeSpan.Zero : TimeSpan.FromMilliseconds(left);
    }

    public bool HasIp(string? ip) =>
        !string.IsNullOrWhiteSpace(ip)
        && Ips.Any(i => string.Equals(i, ip, StringComparison.OrdinalIgnoreCase));

    public bool HasNick(string? nick)
    {
        var normalized = ServerConfig.NormalizeNick(nick);
        if (normalized.Length == 0)
            return false;

        return string.Equals(Nick, normalized, StringComparison.OrdinalIgnoreCase)
               || Aliases.Any(a => string.Equals(a, normalized, StringComparison.OrdinalIgnoreCase));
    }

    public bool HasMatchId(int id) => id != 0 && MatchIds.Contains(id);

    /// <summary>Ник для логов/чата; если бана по нику не было — первый IP.</summary>
    [JsonIgnore]
    public string DisplayNick =>
        string.IsNullOrWhiteSpace(Nick) ? Ips.FirstOrDefault() ?? "?" : Nick;
}

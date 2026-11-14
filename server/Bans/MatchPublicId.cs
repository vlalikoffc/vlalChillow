using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using StandChillow.LanServer.Config;

namespace StandChillow.LanServer.Bans;

/// <summary>
/// Ник → стабильный серверный id (1…100 000 000).
/// </summary>
/// <remarks>
/// <para>
/// Это чистая функция от ника: один и тот же ник всегда даёт один и тот же id — после
/// рестарта, после удаления каталога сервера, на другой машине. Никакого состояния и
/// никакого рандома, поэтому «разойтись в расчётах» нечему.
/// </para>
/// <para>
/// Вход — ник после <see cref="ServerConfig.NormalizeNick"/> (NFC, без невидимых
/// символов, пробелы схлопнуты), приведённый к верхнему регистру инвариантной культурой,
/// в UTF-8. Поэтому эмодзи, кириллица, пробелы и любые кастомные символы участвуют целиком
/// и одинаково на любой платформе, а «Влал» и «влал» — одна личность. Регистр в id не
/// входит намеренно: бан по нику и так сравнивается без учёта регистра, и id обязан
/// означать того же человека.
/// </para>
/// <para>
/// 32-битный диапазон 1…100 000 000 — это ~100 млн бакетов: на сервере с сотнями
/// игроков коллизия маловероятна, но теоретически возможна. Поэтому id — селектор,
/// а не ключ бана: <c>/ban</c> всегда приводит его к нику и IP.
/// </para>
/// </remarks>
public static class MatchPublicId
{
    public const int Min = 1;
    public const int Max = 100_000_000;

    /// <summary>Стабильный id для ника; <c>0</c> — пустой ник (id не выдаётся).</summary>
    public static int ForNick(string? nick)
    {
        // Два шага и оба чистые: NFC + снятие невидимых/лишних пробелов, затем
        // инвариантный верхний регистр и снова NFC (верхний регистр может дать
        // не-нормализованную форму для составных символов).
        var normalized = ServerConfig.NormalizeNick(nick).ToUpperInvariant();
        normalized = normalized.Normalize(NormalizationForm.FormC);
        if (normalized.Length == 0)
            return 0;

        var bytes = Encoding.UTF8.GetBytes(normalized);

        // SHA-256 + little-endian чтение: результат не зависит от платформы.
        var hash = SHA256.HashData(bytes);
        var value = BinaryPrimitives.ReadUInt64LittleEndian(hash);

        const ulong span = (ulong)(Max - Min + 1);
        return (int)(value % span) + Min;
    }

    /// <summary>
    /// Значение, которое сервер отдаёт в actor-свойстве <c>uid</c> вместо клиентского
    /// «Offline»: десятичная строка id (клиент рисует это поле как номер игрока).
    /// </summary>
    /// <remarks>
    /// Строкой, а не числом: в захватах поле <c>uid</c> — String, и любой разбор
    /// <c>int.TryParse</c> на стороне клиента такую строку принимает. Пустой ник → false,
    /// тогда исходное значение не трогаем.
    /// </remarks>
    public static bool TryWireUid(string? nick, out string uid)
    {
        var id = ForNick(nick);
        if (id == 0)
        {
            uid = string.Empty;
            return false;
        }

        uid = id.ToString(CultureInfo.InvariantCulture);
        return true;
    }

    /// <summary>Короткая форма для логов.</summary>
    public static string Describe(string? nick)
    {
        var id = ForNick(nick);
        return id == 0 ? "—" : $"#{id}";
    }
}

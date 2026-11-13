using System.Buffers.Binary;
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
/// символов, пробелы схлопнуты, регистр сохраняется) в UTF-8. Поэтому эмодзи,
/// кириллица, пробелы и любые кастомные символы участвуют целиком и одинаково на
/// любой платформе.
/// </para>
/// <para>
/// 32-битный диапазон 1…100 000 000 — это ~100 млн бакетов: на сервере с сотнями
/// игроков коллизия маловероятна, но теоретически возможна. Поэтому id — селектор,
/// а не ключ бана: <c>/banid</c> всегда приводит его к нику и IP.
/// </para>
/// </remarks>
public static class MatchPublicId
{
    public const int Min = 1;
    public const int Max = 100_000_000;

    /// <summary>Стабильный id для ника; <c>0</c> — пустой ник (id не выдаётся).</summary>
    public static int ForNick(string? nick)
    {
        var normalized = ServerConfig.NormalizeNick(nick);
        if (normalized.Length == 0)
            return 0;

        var bytes = Encoding.UTF8.GetBytes(normalized);

        // SHA-256 + little-endian чтение: результат не зависит от платформы.
        var hash = SHA256.HashData(bytes);
        var value = BinaryPrimitives.ReadUInt64LittleEndian(hash);

        const ulong span = (ulong)(Max - Min + 1);
        return (int)(value % span) + Min;
    }

    /// <summary>Короткая форма для логов.</summary>
    public static string Describe(string? nick)
    {
        var id = ForNick(nick);
        return id == 0 ? "—" : $"#{id}";
    }
}

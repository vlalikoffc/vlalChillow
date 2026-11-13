using System.Net;
using System.Security.Cryptography;
using System.Text;

namespace StandChillow.LanServer.Net;

/// <summary>
/// Авторизация админа на этот запуск сервера: одноразовый код + список IP, которым он уже
/// предъявлен. Ничего не пишется на диск — после перезапуска код новый и все IP забыты.
/// </summary>
/// <remarks>
/// <para>
/// Зачем: ник присылает клиент, поэтому ник «влал» может взять кто угодно. Раньше это давало
/// ему админ-команды (и `/ban`). Теперь ник — только повод спросить код; права даёт IP,
/// который этот код ввёл.
/// </para>
/// <para>
/// Код: 24 символа латиницы и цифр из
/// <see cref="RandomNumberGenerator"/>. Без похожих друг на друга символов (0/O, 1/l/I),
/// чтобы его можно было продиктовать и ввести с телефона. Сравнение — без учёта регистра и
/// в постоянном времени.
/// </para>
/// </remarks>
public sealed class AdminAuth
{
    /// <summary>Длина кода по умолчанию (символов).</summary>
    public const int DefaultCodeLength = 24;

    /// <summary>Алфавит кода: латиница + цифры, без неоднозначных символов.</summary>
    public const string Alphabet =
        "ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnopqrstuvwxyz23456789";

    private readonly object _gate = new();
    private readonly HashSet<IPAddress> _authorized = new();
    private string _code;

    /// <summary>Новый случайный код.</summary>
    public AdminAuth()
        : this(GenerateCode(DefaultCodeLength))
    {
    }

    /// <summary>Код задан снаружи (конфиг/тесты).</summary>
    public AdminAuth(string code)
    {
        if (string.IsNullOrWhiteSpace(code))
            throw new ArgumentException("код админа не может быть пустым", nameof(code));

        _code = code.Trim();
    }

    /// <summary>Текущий код. Живёт до перезапуска (или до явного сброса).</summary>
    public string Code
    {
        get { lock (_gate) return _code; }
    }

    /// <summary>Сколько адресов уже авторизовано на этом запуске.</summary>
    public int AuthorizedCount
    {
        get { lock (_gate) return _authorized.Count; }
    }

    /// <summary>Авторизованные адреса (для консоли).</summary>
    public IReadOnlyList<IPAddress> AuthorizedIps
    {
        get { lock (_gate) return _authorized.ToList(); }
    }

    /// <summary>Сгенерировать код: только латиница и цифры из <see cref="Alphabet"/>.</summary>
    public static string GenerateCode(int length)
    {
        if (length <= 0)
            throw new ArgumentOutOfRangeException(nameof(length));

        var chars = new char[length];
        for (var i = 0; i < length; i++)
            chars[i] = Alphabet[RandomNumberGenerator.GetInt32(Alphabet.Length)];
        return new string(chars);
    }

    /// <summary>Этот адрес уже авторизован?</summary>
    public bool IsAuthorized(IPAddress? ip)
    {
        if (ip is null)
            return false;

        lock (_gate)
            return _authorized.Contains(Canonical(ip));
    }

    /// <summary>Авторизовать адрес. <c>true</c> — если его не было в списке.</summary>
    public bool Authorize(IPAddress? ip)
    {
        if (ip is null)
            return false;

        lock (_gate)
        {
            // IPv4-mapped IPv6 приходит с разных сокетов: 127.0.0.1 и ::ffff:127.0.0.1 —
            // один и тот же адрес, и авторизация должна переживать это.
            return _authorized.Add(Canonical(ip));
        }
    }

    /// <summary>Код принят? Сравнение без учёта регистра, в постоянном времени.</summary>
    public bool Matches(string? text)
    {
        var candidate = (text ?? string.Empty).Trim();
        if (candidate.Length == 0)
            return false;

        lock (_gate)
            return FixedTimeEquals(_code, candidate);
    }

    /// <summary>Новый код и полный сброс авторизаций.</summary>
    public string Reset(string? code = null)
    {
        var next = string.IsNullOrWhiteSpace(code)
            ? GenerateCode(DefaultCodeLength)
            : code.Trim();

        lock (_gate)
        {
            _authorized.Clear();
            _code = next;
            return _code;
        }
    }

    private static bool FixedTimeEquals(string expected, string actual)
    {
        // Регистр не важен: код диктуют голосом и вводят с телефона.
        var a = Encoding.UTF8.GetBytes(expected.ToUpperInvariant());
        var b = Encoding.UTF8.GetBytes(actual.ToUpperInvariant());
        return a.Length == b.Length && CryptographicOperations.FixedTimeEquals(a, b);
    }

    private static IPAddress Canonical(IPAddress ip) =>
        ip.IsIPv4MappedToIPv6 ? ip.MapToIPv4() : ip;
}

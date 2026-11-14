using System.Net;
using System.Net.Sockets;
using StandChillow.LanServer.Lan;

namespace StandChillow.LanServer.Net;

/// <summary>
/// Какой адрес показать игроку в ответе «комната не найдена — создать можно здесь».
/// </summary>
/// <remarks>
/// <para>
/// Порядок: явный <c>web.publicBaseUrl</c> из <c>config.json</c> → внешний IP (только если
/// он не локальный) → LAN-адрес машины. Настоящий домен и IP <b>не</b> зашиты в код:
/// они живут в gitignored <c>config.json</c> (CONTRIBUTING.md, «не дразнить ддос»).
/// </para>
/// <para>
/// Определение внешнего IP кэшируется на <see cref="ExternalIpCacheTtl"/> и ограничено
/// <see cref="ExternalIpTimeout"/>: лобби-чат не должен зависать из-за чужого сервиса, а
/// запросов к нему не должно быть по одному на каждую опечатку игрока.
/// </para>
/// <para>
/// Транспорт внешнего IP подменяем (<see cref="ExternalIpProvider"/>) — в тестах сети нет
/// вообще, поэтому проверяется порядок решений, а не доступность интернета.
/// </para>
/// </remarks>
public sealed class SiteAddressResolver
{
    /// <summary>Сколько живёт определённый внешний IP.</summary>
    public static readonly TimeSpan ExternalIpCacheTtl = TimeSpan.FromMinutes(10);

    /// <summary>Сколько ждём внешний сервис, прежде чем взять LAN-адрес.</summary>
    public static readonly TimeSpan ExternalIpTimeout = TimeSpan.FromSeconds(2);

    private readonly object _gate = new();
    private readonly Func<DateTime> _clock;
    private string? _cachedExternal;
    private DateTime _cachedAtUtc = DateTime.MinValue;
    private bool _externalFailed;

    public SiteAddressResolver(Func<DateTime>? clock = null)
    {
        _clock = clock ?? (() => DateTime.UtcNow);
    }

    /// <summary>Явный адрес из конфига (<c>web.publicBaseUrl</c>); пусто — не задан.</summary>
    public string ConfiguredBaseUrl { get; init; } = "";

    /// <summary>Как узнать внешний IP; <c>null</c> — не спрашивать (тесты, офлайн-сервер).</summary>
    public Func<CancellationToken, Task<string?>>? ExternalIpProvider { get; init; }

    /// <summary>
    /// LAN-адрес машины. <c>null</c> (не задан) — спросить <c>LanInterfacePicker</c>;
    /// заданный делегат, вернувший <c>null</c>, означает «LAN-адреса нет» и в сеть
    /// больше не ходим. Разница важна: иначе тест не может запретить реальный сетевой
    /// интерфейс, и результат зависит от машины, на которой он запущен.
    /// </summary>
    public Func<string?>? LanAddressProvider { get; init; }

    /// <summary>Что показать игроку.</summary>
    public string Resolve()
    {
        if (!string.IsNullOrWhiteSpace(ConfiguredBaseUrl))
            return ConfiguredBaseUrl.Trim();

        var external = TryGetExternalIp();
        if (!string.IsNullOrEmpty(external))
            return external;

        // Заданный делегат — окончательный ответ, даже если это null:
        // «LAN-адреса нет» не повод лезть в реальные интерфейсы.
        var lan = LanAddressProvider is null ? LanAddressFallback() : LanAddressProvider();
        return string.IsNullOrEmpty(lan) ? "localhost" : lan;
    }

    /// <summary>Внешний IP или <c>null</c> (локальный адрес внешним не считается).</summary>
    public string? TryGetExternalIp()
    {
        lock (_gate)
        {
            var now = _clock();
            if (_cachedExternal is not null && now - _cachedAtUtc < ExternalIpCacheTtl)
                return _cachedExternal;

            // Уже падало в этом окне — не долбим сервис на каждую опечатку в /join.
            if (_externalFailed && now - _cachedAtUtc < ExternalIpCacheTtl)
                return null;
        }

        if (ExternalIpProvider is null)
            return null;

        string? value = null;
        try
        {
            using var cts = new CancellationTokenSource(ExternalIpTimeout);
            // Не блокируем UDP-цикл на время запроса: ждём ограниченно и только здесь.
            // Wait(TimeSpan) возвращает bool — берём Result только если дождались.
            var task = ExternalIpProvider(cts.Token);
            value = task.Wait(ExternalIpTimeout) ? task.Result : null;
        }
        catch (Exception)
        {
            value = null;
        }

        var normalized = NormalizeHostValue(value);
        lock (_gate)
        {
            _cachedAtUtc = _clock();
            if (normalized is null || IsLocalAddress(normalized))
            {
                // За NAT/VPN внешний сервис может вернуть локальный адрес — это не адрес
                // для игрока, поэтому идём в LAN-ветку.
                _externalFailed = true;
                _cachedExternal = null;
                return null;
            }

            _externalFailed = false;
            _cachedExternal = normalized;
            return normalized;
        }
    }

    /// <summary>Сбросить кэш (адрес сменился, сеть переподключилась).</summary>
    public void Invalidate()
    {
        lock (_gate)
        {
            _cachedExternal = null;
            _cachedAtUtc = DateTime.MinValue;
            _externalFailed = false;
        }
    }

    /// <summary>
    /// Привести ответ сервиса к хосту: обрезать пробелы/кавычки, отбросить мусор и
    /// оставить только IP или имя. Пустое → <c>null</c>.
    /// </summary>
    public static string? NormalizeHostValue(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return null;

        var text = raw.Trim().Trim('"', '\'', '\r', '\n', '\t', ' ');
        if (text.Length == 0 || text.Length > 253)
            return null;

        // Сервис мог вернуть HTML/ошибку — принимаем только «похожее на хост».
        foreach (var ch in text)
        {
            var ok = ch is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9'
                     or '.' or '-' or ':' or '[' or ']';
            if (!ok)
                return null;
        }

        return text;
    }

    /// <summary>Локальный/приватный адрес — не годится как «внешний».</summary>
    public static bool IsLocalAddress(string host)
    {
        var bare = host;
        var colon = bare.LastIndexOf(':');
        if (colon > 0 && !bare.StartsWith('[') && bare.IndexOf(':') == colon)
            bare = bare[..colon];

        if (!IPAddress.TryParse(bare.Trim('[', ']'), out var ip))
            return false;

        if (IPAddress.IsLoopback(ip))
            return true;

        if (ip.AddressFamily == AddressFamily.InterNetworkV6)
            return ip.IsIPv6LinkLocal || ip.IsIPv6SiteLocal;

        var b = ip.GetAddressBytes();
        return b[0] == 10
               || (b[0] == 172 && b[1] >= 16 && b[1] <= 31)
               || (b[0] == 192 && b[1] == 168)
               || (b[0] == 169 && b[1] == 254)
               || b[0] == 127;
    }

    private static string? LanAddressFallback()
    {
        try
        {
            var ip = LanInterfacePicker.PickLanBindAddress();
            return ip?.ToString();
        }
        catch (Exception)
        {
            return null;
        }
    }
}

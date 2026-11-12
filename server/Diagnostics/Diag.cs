using StandChillow.LanServer.Config;

namespace StandChillow.LanServer.Diagnostics;

/// <summary>
/// Управление объёмом диагностики в горячих путях (сетевой цикл, ретрансляция пакетов).
/// </summary>
/// <remarks>
/// Каждая строка <see cref="Console.WriteLine"/> в горячем пути — это запись в терминал
/// и логи, то есть работа сетевого потока. На 4 игроках это терпимо, на 10+ — именно
/// тот самый «пинг 200 и сервер перестал слать данные». Поэтому подробности выключены
/// по умолчанию и включаются блоком <c>debug</c> в <c>config.json</c>.
/// </remarks>
public static class Diag
{
    /// <summary>
    /// Флаги, заданные командной строкой или окружением. Залипают на процесс:
    /// <c>config reload</c> не может их выключить.
    /// </summary>
    public static bool CliVerbose { get; set; }

    /// <summary><c>--verbose</c> просил captures вместе с логом (старое поведение).</summary>
    public static bool CapturesForced { get; set; }

    /// <summary>Подробный per-packet трейс (<c>debug.verbose</c>).</summary>
    public static bool Verbose { get; set; }

    /// <summary>Периодическая сводка <c>[health]</c> (<c>debug.health</c>).</summary>
    public static bool StatsEnabled { get; set; } = true;

    /// <summary>Логировать каждый принятый пакет (<c>debug.logPackets</c>).</summary>
    public static bool LogPackets { get; set; }

    /// <summary>Логировать каждый discovery-probe (<c>debug.logDiscovery</c>).</summary>
    public static bool LogDiscovery { get; set; }

    /// <summary>Отладочные сообщения хоста в чат (<c>debug.matchChat</c>).</summary>
    public static bool MatchChat { get; set; }

    /// <summary>Писать ли per-packet трейс (verbose или явный logPackets).</summary>
    public static bool LogVerbose => Verbose || LogPackets;

    /// <summary>Писать ли трейс discovery (verbose или явный logDiscovery).</summary>
    public static bool LogDiscoveryVerbose => Verbose || LogDiscovery;

    /// <summary>Интервал строки <c>[health]</c> в секундах (0 — выключено).</summary>
    public static int HealthIntervalSec { get; set; } = 10;

    /// <summary>
    /// Применяет блоки <c>debug</c> и <c>tuning</c>. Только выделенный сервер;
    /// безопасно вызывать повторно из <c>config reload</c>.
    /// </summary>
    /// <param name="debug">Блок <c>debug</c>.</param>
    /// <param name="healthIntervalSec">Интервал <c>[health]</c> из <c>tuning</c>.</param>
    /// <param name="launchVerbose">Запуск просил трейс (<c>launch.verbose</c>).</param>
    public static void Apply(DebugConfig debug, int healthIntervalSec, bool launchVerbose = false)
    {
        Verbose = CliVerbose || launchVerbose || debug.Verbose;
        HealthIntervalSec = healthIntervalSec;
        StatsEnabled = debug.Health && healthIntervalSec > 0;
        LogPackets = debug.LogPackets;
        LogDiscovery = debug.LogDiscovery;
        MatchChat = debug.MatchChat;
    }

    /// <summary>Читает флаги окружения: <c>VLAL_VERBOSE=1</c>.</summary>
    public static void ApplyEnvironment()
    {
        var v = Environment.GetEnvironmentVariable("VLAL_VERBOSE");
        if (!string.IsNullOrWhiteSpace(v)
            && v.Trim() is "1" or "true" or "TRUE" or "True" or "yes" or "on" or "ON")
        {
            CliVerbose = true;
            Verbose = true;
        }
    }
}

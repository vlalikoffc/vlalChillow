namespace StandChillow.LanServer.Diagnostics;

/// <summary>
/// Управление объёмом диагностики в горячих путях (сетевой цикл, ретрансляция пакетов).
/// </summary>
/// <remarks>
/// Каждая строка <see cref="Console.WriteLine"/> в горячем пути — это синхронная запись
/// в <b>терминал + два файла</b> с <c>AutoFlush</c> под общим локом, то есть блокировка
/// сетевого потока на дисковом вводе-выводе. На 4 игроках это терпимо, на 10+ — именно
/// тот самый «пинг 200 и сервер перестал слать данные». Поэтому:
///
/// <list type="bullet">
///   <item><see cref="Verbose"/> — подробный трейс каждого пакета (по умолчанию <c>false</c>);</item>
///   <item><see cref="StatsEnabled"/> — периодическая сводка счётчиков (по умолчанию <c>true</c>);</item>
///   <item>Критичные события (старт/стоп, ошибки, смена фазы раунда, фолы) пишутся всегда.</item>
/// </list>
///
/// Включается через <c>--verbose</c> / <c>VLAL_VERBOSE=1</c>. Провод (формат пакетов)
/// не меняется: выключается только текст в консоль/лог.
/// </remarks>
public static class Diag
{
    /// <summary>Подробный per-packet трейс. Включать только при отладке протокола.</summary>
    public static bool Verbose { get; set; }

    /// <summary>Периодическая сводка (раз в N секунд) — дешёвая и полезная.</summary>
    public static bool StatsEnabled { get; set; } = true;

    /// <summary>Писать ли строку подробного трейса.</summary>
    public static bool LogVerbose => Verbose;

    /// <summary>Читает флаги окружения: <c>VLAL_VERBOSE=1</c>.</summary>
    public static void ApplyEnvironment()
    {
        var v = Environment.GetEnvironmentVariable("VLAL_VERBOSE");
        if (!string.IsNullOrWhiteSpace(v)
            && v.Trim() is "1" or "true" or "TRUE" or "True" or "yes" or "on" or "ON")
            Verbose = true;
    }
}

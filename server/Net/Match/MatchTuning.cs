using StandChillow.LanServer.Config;

namespace StandChillow.LanServer.Net.Match;

/// <summary>
/// Горячие ручки выделенного хоста (темп цикла, порог бэкапа, пул пакетов).
/// </summary>
/// <remarks>
/// Читается <b>только</b> из <see cref="ServerConfig.Tuning"/> и только на старте
/// выделенного сервера. Проба (<c>--client</c>) сюда не пишет и отсюда не читает —
/// у неё свои тайминги и свой capture-поток.
/// </remarks>
public static class MatchTuning
{
    /// <summary>Период игрового цикла хоста, мс.</summary>
    public static int TickMs { get; private set; } = 33;

    /// <summary>Частота рассылки поз, мс.</summary>
    public static int StateIntervalMs { get; private set; } = 50;

    /// <summary>Порог очереди надёжных пакетов для дропа косметики.</summary>
    public static int ReliableQueueSoftLimit { get; private set; } = 256;

    /// <summary>Пул пакетов LiteNetLib.</summary>
    public static int PacketPoolSize { get; private set; } = 4096;

    /// <summary>Таймаут отключения пира, мс.</summary>
    public static int DisconnectTimeoutMs { get; private set; } = 10_000;

    /// <summary>UpdateTime LiteNetLib, мс.</summary>
    public static int NetUpdateTimeMs { get; private set; } = 15;

    /// <summary>PingInterval LiteNetLib, мс.</summary>
    public static int PingIntervalMs { get; private set; } = 1000;

    public static TimeSpan Tick => TimeSpan.FromMilliseconds(TickMs);

    public static TimeSpan StateInterval => TimeSpan.FromMilliseconds(StateIntervalMs);

    /// <summary>Значения блока <c>tuning</c>. Чистая функция — без побочных эффектов.</summary>
    public readonly record struct TuningSnapshot(
        int TickMs,
        int StateIntervalMs,
        int ReliableQueueSoftLimit,
        int PacketPoolSize,
        int DisconnectTimeoutMs,
        int NetUpdateTimeMs,
        int PingIntervalMs);

    /// <summary>Раскладывает конфиг по полям (те же пределы, что в <c>Normalize</c>).</summary>
    public static TuningSnapshot Resolve(TuningConfig tuning) => new(
        TickMs: tuning.TickMs,
        StateIntervalMs: tuning.StateIntervalMs,
        ReliableQueueSoftLimit: tuning.ReliableQueueSoftLimit,
        PacketPoolSize: tuning.PacketPoolSize,
        DisconnectTimeoutMs: tuning.DisconnectTimeoutMs,
        NetUpdateTimeMs: tuning.NetUpdateTimeMs,
        PingIntervalMs: tuning.PingIntervalMs);

    /// <summary>Применить блок <c>tuning</c>. Вызывается только на выделенном сервере.</summary>
    public static void Apply(TuningConfig tuning)
    {
        var t = Resolve(tuning);
        TickMs = t.TickMs;
        StateIntervalMs = t.StateIntervalMs;
        ReliableQueueSoftLimit = t.ReliableQueueSoftLimit;
        PacketPoolSize = t.PacketPoolSize;
        DisconnectTimeoutMs = t.DisconnectTimeoutMs;
        NetUpdateTimeMs = t.NetUpdateTimeMs;
        PingIntervalMs = t.PingIntervalMs;
    }
}

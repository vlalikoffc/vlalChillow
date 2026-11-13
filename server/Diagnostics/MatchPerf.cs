using System.Diagnostics;
using System.Globalization;

namespace StandChillow.LanServer.Diagnostics;

/// <summary>
/// Дешёвая статистика горячего цикла матча для строки <c>[health]</c> (issue #35):
/// сколько реально длится тик, сколько из него занимают приём пакетов и фазовый автомат,
/// какие опкоды идут потоком и как часто GC собирает мусор.
/// </summary>
/// <remarks>
/// Горячий путь только пишет счётчики через <see cref="Interlocked"/> — без блокировок,
/// без форматирования строк и без аллокаций. Текст собирается раз в <c>health</c> (10 с)
/// на том же poll-потоке, поэтому снимок окна не конкурирует с обработкой пакетов.
/// <para>
/// Счётчики <b>не влияют на провод</b>: ни один опкод, тайминг или пакет не меняется.
/// </para>
/// </remarks>
public static class MatchPerf
{
    private static readonly object WindowGate = new();
    private static readonly long[] RxByOpcode = new long[256];
    private static readonly long[] PrevRxByOpcode = new long[256];

    private static long _ticks;
    private static long _tickUsSum;
    private static long _tickUsMax;
    private static long _slowTicks;
    private static long _pollUsSum;
    private static long _flowUsSum;
    private static long _rxPackets;
    private static long _rxBytes;

    private static long _prevTicks;
    private static long _prevTickUsSum;
    private static long _prevPollUsSum;
    private static long _prevFlowUsSum;
    private static long _prevSlowTicks;
    private static long _prevRxPackets;
    private static long _prevRxBytes;

    private static long _prevGc0;
    private static long _prevAllocatedBytes;
    private static long _lastSnapshotTs = Stopwatch.GetTimestamp();

    private static long _slowLogged;
    private static long _lastSlowLogTs;

    /// <summary>
    /// Тик длиннее этого значения считается медленным (по умолчанию 2 × 33 мс).
    /// Задаётся из poll-цикла по <c>tuning.tickMs</c>.
    /// </summary>
    public static int SlowTickMs { get; set; } = 66;

    /// <summary>Сколько медленных тиков накопилось за запуск.</summary>
    public static long SlowTicks => Interlocked.Read(ref _slowTicks);

    /// <summary>Один тик матч-хоста: общая длительность, из неё приём и фазовый автомат.</summary>
    public static void RecordTick(double totalMs, double pollMs, double flowMs)
    {
        var totalUs = Micros(totalMs);
        Interlocked.Increment(ref _ticks);
        Interlocked.Add(ref _tickUsSum, totalUs);
        Interlocked.Add(ref _pollUsSum, Micros(pollMs));
        Interlocked.Add(ref _flowUsSum, Micros(flowMs));
        UpdateMax(ref _tickUsMax, totalUs);
        if (totalMs > SlowTickMs)
            Interlocked.Increment(ref _slowTicks);
    }

    /// <summary>Один принятый матч-пакет: опкод и размер. Вызывается из сетевого пути.</summary>
    public static void RecordRx(byte opcode, int bytes)
    {
        Interlocked.Increment(ref _rxPackets);
        Interlocked.Add(ref _rxBytes, bytes);
        Interlocked.Increment(ref RxByOpcode[opcode]);
    }

    /// <summary>Был ли тик длиннее <see cref="SlowTickMs"/>.</summary>
    public static bool IsSlowTick(double totalMs) => totalMs > SlowTickMs;

    /// <summary>
    /// Не залить лог медленными тиками: первые 5, дальше каждый 50-й, но не чаще
    /// одного раза в 3 секунды. Сами тики при этом продолжают считаться.
    /// </summary>
    public static bool ShouldLogSlowTick()
    {
        var count = Interlocked.Increment(ref _slowLogged);
        if (count > 5 && count % 50 != 0)
            return false;

        var now = Stopwatch.GetTimestamp();
        var prev = Interlocked.Read(ref _lastSlowLogTs);
        if (prev != 0 && now - prev < Stopwatch.Frequency * 3)
            return false;

        Interlocked.Exchange(ref _lastSlowLogTs, now);
        return true;
    }

    /// <summary>
    /// Снимок окна с прошлого вызова (обычно 10 с): средний/максимальный тик, доли
    /// приёма и фаз, скорость RX, топ опкодов, давление GC и рабочий набор.
    /// </summary>
    /// <param name="opcodeName">
    /// Необязательный преобразователь опкода в имя — вызывается максимум 3 раза за
    /// снимок, поэтому строки горячего пути не трогает.
    /// </param>
    public static string DescribeWindow(Func<byte, string>? opcodeName = null)
    {
        lock (WindowGate)
        {
            var now = Stopwatch.GetTimestamp();
            var elapsedSec = Math.Max(0.001, (now - _lastSnapshotTs) / (double)Stopwatch.Frequency);
            _lastSnapshotTs = now;

            var ticks = Interlocked.Read(ref _ticks) - _prevTicks;
            var tickUsSum = Interlocked.Read(ref _tickUsSum) - _prevTickUsSum;
            var maxUs = Interlocked.Exchange(ref _tickUsMax, 0);
            var slow = Interlocked.Read(ref _slowTicks) - _prevSlowTicks;
            var pollUsSum = Interlocked.Read(ref _pollUsSum) - _prevPollUsSum;
            var flowUsSum = Interlocked.Read(ref _flowUsSum) - _prevFlowUsSum;
            var rx = Interlocked.Read(ref _rxPackets) - _prevRxPackets;
            var rxBytes = Interlocked.Read(ref _rxBytes) - _prevRxBytes;

            _prevTicks = Interlocked.Read(ref _ticks);
            _prevTickUsSum = Interlocked.Read(ref _tickUsSum);
            _prevPollUsSum = Interlocked.Read(ref _pollUsSum);
            _prevFlowUsSum = Interlocked.Read(ref _flowUsSum);
            _prevSlowTicks = Interlocked.Read(ref _slowTicks);
            _prevRxPackets = Interlocked.Read(ref _rxPackets);
            _prevRxBytes = Interlocked.Read(ref _rxBytes);

            var tickAvg = ticks > 0 ? tickUsSum / (double)ticks / 1000.0 : 0.0;
            var pollAvg = ticks > 0 ? pollUsSum / (double)ticks / 1000.0 : 0.0;
            var flowAvg = ticks > 0 ? flowUsSum / (double)ticks / 1000.0 : 0.0;

            var gc0 = GC.CollectionCount(0);
            var allocated = GC.GetTotalAllocatedBytes(precise: false);
            var gc0Delta = gc0 - _prevGc0;
            var allocDelta = Math.Max(0, allocated - _prevAllocatedBytes);
            _prevGc0 = gc0;
            _prevAllocatedBytes = allocated;
            var allocKbPerSec = allocDelta / 1024.0 / elapsedSec;

            var top = TopOpcodes();
            var topText = top.Count == 0
                ? "none"
                : string.Join(",", top.Select(t =>
                    $"{opcodeName?.Invoke(t.Op) ?? t.Op.ToString(CultureInfo.InvariantCulture)}:{t.Count}"));

            return string.Concat(
                "tick avg=", F(tickAvg), "ms max=", F(maxUs / 1000.0), "ms slow=",
                slow.ToString(CultureInfo.InvariantCulture),
                "/", ticks.ToString(CultureInfo.InvariantCulture),
                " poll=", F(pollAvg), "ms flow=", F(flowAvg), "ms",
                " rx=", rx.ToString(CultureInfo.InvariantCulture),
                " (", F(rxBytes / 1024.0 / elapsedSec), "KB/s) op=", topText,
                " gc0=", gc0Delta.ToString(CultureInfo.InvariantCulture),
                " alloc=", F(allocKbPerSec), "KB/s",
                " ws=", F(Environment.WorkingSet / (1024.0 * 1024.0)), "MB",
                " threads=", ThreadPool.ThreadCount.ToString(CultureInfo.InvariantCulture));
        }
    }

    private static List<(byte Op, long Count)> TopOpcodes()
    {
        var top = new List<(byte Op, long Count)>(3);
        for (var i = 0; i < RxByOpcode.Length; i++)
        {
            var count = Interlocked.Read(ref RxByOpcode[i]) - PrevRxByOpcode[i];
            PrevRxByOpcode[i] = Interlocked.Read(ref RxByOpcode[i]);
            if (count <= 0)
                continue;

            if (top.Count < 3)
            {
                top.Add(((byte)i, count));
                top.Sort((a, b) => b.Count.CompareTo(a.Count));
                continue;
            }

            if (count <= top[^1].Count)
                continue;
            top[^1] = ((byte)i, count);
            top.Sort((a, b) => b.Count.CompareTo(a.Count));
        }

        return top;
    }

    /// <summary>Сброс счётчиков (только тесты; в рантайме не вызывается).</summary>
    internal static void ResetForTests()
    {
        lock (WindowGate)
        {
            _ticks = 0;
            _tickUsSum = 0;
            _tickUsMax = 0;
            _slowTicks = 0;
            _pollUsSum = 0;
            _flowUsSum = 0;
            _rxPackets = 0;
            _rxBytes = 0;
            _prevTicks = 0;
            _prevTickUsSum = 0;
            _prevPollUsSum = 0;
            _prevFlowUsSum = 0;
            _prevSlowTicks = 0;
            _prevRxPackets = 0;
            _prevRxBytes = 0;
            _prevGc0 = GC.CollectionCount(0);
            _prevAllocatedBytes = GC.GetTotalAllocatedBytes(precise: false);
            _lastSnapshotTs = Stopwatch.GetTimestamp();
            _slowLogged = 0;
            _lastSlowLogTs = 0;
            Array.Clear(RxByOpcode);
            Array.Clear(PrevRxByOpcode);
        }
    }

    private static long Micros(double ms) => (long)Math.Round(ms * 1000.0);

    private static string F(double value) => value.ToString("0.0", CultureInfo.InvariantCulture);

    private static void UpdateMax(ref long target, long value)
    {
        var current = Interlocked.Read(ref target);
        while (value > current)
        {
            var seen = Interlocked.CompareExchange(ref target, value, current);
            if (seen == current)
                return;
            current = seen;
        }
    }
}

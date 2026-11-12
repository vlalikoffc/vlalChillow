using System.Collections.Concurrent;

namespace StandChillow.LanServer.Diagnostics;

/// <summary>
/// Асинхронный приёмник диагностических capture-файлов (по одному <c>.bin</c> на пакет).
/// </summary>
/// <remarks>
/// Раньше каждый пакет писался на диск <b>синхронно прямо из сетевого потока</b>
/// (<c>File.WriteAllBytes</c> + <c>Console.WriteLine</c> с AutoFlush в два лога).
/// На 10+ игроках это тысячи файловых операций в секунду: поток опроса LiteNetLib
/// перестаёт вычитывать сокет, RX-буфер переполняется, надёжные пакеты уходят в
/// ретрансмит (пинг растёт), очередь событий копится — сервер «замирает».
///
/// Здесь: выключено по умолчанию, при включении — <b>очередь + один писатель</b>,
/// жёсткий бюджет по файлам/байтам и молчаливый сброс при переполнении. Сетевой
/// поток никогда не ждёт диск. Формат пакетов и поведение клиентов не затрагиваются.
/// </remarks>
public static class CaptureSink
{
    private sealed record Job(string Tag, byte[] Payload);

    private const int QueueCapacity = 8192;

    private static readonly BlockingCollection<Job> Queue =
        new(new ConcurrentQueue<Job>(), QueueCapacity);

    private static readonly object StateGate = new();
    private static Task? _writer;
    private static string _dir = string.Empty;

    private static long _dropped;
    private static long _written;
    private static long _skippedByBudget;
    private static long _bytes;
    private static int _files;
    private static int _index;
    private static int _disabledByBudget;

    /// <summary>Сколько файлов максимум за запуск (защита от забивания диска).</summary>
    public static int MaxFiles { get; set; } = 4000;

    /// <summary>Сколько байт максимум за запуск.</summary>
    public static long MaxBytes { get; set; } = 64L * 1024 * 1024;

    /// <summary>Включён ли дамп (по умолчанию <c>false</c> — стабильность важнее).</summary>
    public static bool Enabled { get; private set; }

    public static long Dropped => Interlocked.Read(ref _dropped);
    public static long Written => Interlocked.Read(ref _written);
    public static long SkippedByBudget => Interlocked.Read(ref _skippedByBudget);

    public static string Describe()
    {
        if (!Enabled)
            return "captures=off";
        return $"captures=on dir='{_dir}' written={Written} dropped={Dropped} " +
               $"budgetSkipped={SkippedByBudget} bytes={Interlocked.Read(ref _bytes)}";
    }

    /// <summary>
    /// Включает/выключает дамп. Безопасно вызывать один раз на старте процесса.
    /// </summary>
    public static void Configure(bool enabled, string directory)
    {
        lock (StateGate)
        {
            Enabled = enabled;
            _dir = directory;
            if (!enabled || _writer is not null)
                return;

            try
            {
                Directory.CreateDirectory(directory);
                _writer = Task.Factory.StartNew(
                    WriterLoop,
                    CancellationToken.None,
                    TaskCreationOptions.LongRunning,
                    TaskScheduler.Default);
            }
            catch (Exception ex)
            {
                Enabled = false;
                Console.WriteLine($"[captures] disabled — cannot start writer: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// Поставить пакет в очередь на запись. Никогда не бросает и никогда не блокирует
    /// вызывающий (сетевой) поток: переполнение очереди = пакет пропущен.
    /// </summary>
    public static void Capture(string tag, byte[] payload, string? directory = null)
    {
        if (!Enabled || payload.Length == 0)
            return;

        if (Volatile.Read(ref _disabledByBudget) != 0)
        {
            Interlocked.Increment(ref _skippedByBudget);
            return;
        }

        if (Volatile.Read(ref _files) >= MaxFiles || Interlocked.Read(ref _bytes) >= MaxBytes)
        {
            if (Interlocked.Exchange(ref _disabledByBudget, 1) == 0)
                Console.WriteLine(
                    $"[captures] budget reached (files={Volatile.Read(ref _files)}/{MaxFiles}, " +
                    $"bytes={Interlocked.Read(ref _bytes)}/{MaxBytes}) — further dumps skipped");
            Interlocked.Increment(ref _skippedByBudget);
            return;
        }

        // Каталог запоминается при Configure; необязательный directory оставлен
        // для совместимости и игнорируется.
        _ = directory;

        if (!Queue.TryAdd(new Job(tag, payload)))
            Interlocked.Increment(ref _dropped);
    }

    private static void WriterLoop()
    {
        foreach (var job in Queue.GetConsumingEnumerable())
        {
            try
            {
                var i = Interlocked.Increment(ref _index);
                var path = Path.Combine(
                    _dir,
                    $"{DateTime.UtcNow:yyyyMMdd_HHmmss}_{i:D4}_{Sanitize(job.Tag)}_len{job.Payload.Length}.bin");
                File.WriteAllBytes(path, job.Payload);
                Interlocked.Increment(ref _files);
                Interlocked.Add(ref _bytes, job.Payload.Length);
                Interlocked.Increment(ref _written);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[captures] write failed: {ex.Message}");
            }
        }
    }

    private static string Sanitize(string tag)
    {
        if (string.IsNullOrWhiteSpace(tag))
            return "capture";

        Span<char> buf = stackalloc char[tag.Length];
        for (var i = 0; i < tag.Length; i++)
            buf[i] = char.IsLetterOrDigit(tag[i]) || tag[i] is '_' or '-' ? tag[i] : '_';
        return new string(buf);
    }
}

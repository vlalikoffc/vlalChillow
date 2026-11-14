using System.Net;

namespace StandChillow.LanServer.Web;

/// <summary>
/// Флуд-защита панели: лимит запросов на IP и временная блокировка за упорство.
/// </summary>
/// <remarks>
/// <para>
/// Паролей и куки у панели нет — «один клик и играть». Поэтому защита считает только то,
/// что видно на уровне TCP: адрес. Лимит частоты плюс пауза за серию отказов отсекают
/// скрипт, но не мешают странице с её двумя запросами в секунду.
/// </para>
/// <para>
/// Время инжектируется (<c>clock</c>), поэтому тесты не спят: правило можно проверить
/// без ожидания реальных секунд и без сокетов.
/// </para>
/// <para>
/// Память ограничена: адреса, о которых давно ничего не слышно, вычищаются при обращении.
/// </para>
/// </remarks>
public sealed class WebRateLimiter
{
    private sealed class Bucket
    {
        public DateTime WindowStartUtc;
        public int RequestsInWindow;
        public DateTime? BlockedUntilUtc;
        public readonly List<DateTime> Refusals = new();
        public DateTime LastSeenUtc;
    }

    private readonly object _gate = new();
    private readonly Dictionary<IPAddress, Bucket> _buckets = new();
    private readonly Func<DateTime> _clock;
    private DateTime _lastSweepUtc;

    public WebRateLimiter(Func<DateTime>? clock = null)
    {
        _clock = clock ?? (() => DateTime.UtcNow);
    }

    /// <summary>Запросов в секунду с одного адреса.</summary>
    public int RequestsPerSecond { get; init; } = 10;

    /// <summary>Сколько отказов подряд (в окне) включают паузу.</summary>
    public int RefusalsBeforeBlock { get; init; } = 5;

    /// <summary>Окно подсчёта отказов.</summary>
    public int RefusalWindowSec { get; init; } = 10;

    /// <summary>Длительность паузы.</summary>
    public int BlockDurationSec { get; init; } = 60;

    /// <summary>Сколько помнить адрес без активности.</summary>
    public int IdleEvictionSec { get; init; } = 600;

    /// <summary>
    /// Результат проверки запроса.
    /// </summary>
    /// <param name="Allowed">Пускать ли запрос.</param>
    /// <param name="RetryAfterSec">Сколько ждать (для заголовка <c>Retry-After</c>).</param>
    /// <param name="Reason">Причина отказа для лога; пусто при успехе.</param>
    public readonly record struct Decision(bool Allowed, int RetryAfterSec, string Reason)
    {
        public static Decision Allow() => new(true, 0, "");
        public static Decision Deny(int retry, string reason) => new(false, retry, reason);
    }

    /// <summary>Учесть запрос и решить, пропускать ли его.</summary>
    public Decision Check(IPAddress? address)
    {
        if (address is null)
            return Decision.Allow();

        var now = _clock();
        lock (_gate)
        {
            SweepIfDueLocked(now);

            if (!_buckets.TryGetValue(address, out var bucket))
            {
                bucket = new Bucket
                {
                    WindowStartUtc = now,
                    LastSeenUtc = now,
                };
                _buckets[address] = bucket;
            }

            bucket.LastSeenUtc = now;

            // Заблокирован — только напоминаем, сколько ждать.
            if (bucket.BlockedUntilUtc is { } until && now < until)
                return Decision.Deny(
                    Math.Max(1, (int)Math.Ceiling((until - now).TotalSeconds)),
                    "blocked");

            // Окно в секунду: частота.
            if (now - bucket.WindowStartUtc >= TimeSpan.FromSeconds(1))
            {
                bucket.WindowStartUtc = now;
                bucket.RequestsInWindow = 0;
            }

            bucket.RequestsInWindow++;
            if (bucket.RequestsInWindow > Math.Max(1, RequestsPerSecond))
            {
                RegisterRefusalLocked(bucket, now);
                return Decision.Deny(1, "rate");
            }

            // Успешный проход сбрасывает серию отказов: человек, который пару раз
            // обновил страницу, не должен копить «штраф» всю минуту.
            bucket.Refusals.Clear();
            return Decision.Allow();
        }
    }

    /// <summary>Записать отказ другого рода (плохой запрос, таймаут) в тот же счётчик.</summary>
    public void NoteRefusal(IPAddress? address)
    {
        if (address is null)
            return;

        var now = _clock();
        lock (_gate)
        {
            if (!_buckets.TryGetValue(address, out var bucket))
            {
                bucket = new Bucket { WindowStartUtc = now };
                _buckets[address] = bucket;
            }

            bucket.LastSeenUtc = now;
            RegisterRefusalLocked(bucket, now);
        }
    }

    /// <summary>Сколько адресов сейчас в памяти (для тестов и диагностики).</summary>
    public int TrackedCount
    {
        get
        {
            lock (_gate)
                return _buckets.Count;
        }
    }

    private void RegisterRefusalLocked(Bucket bucket, DateTime now)
    {
        var window = TimeSpan.FromSeconds(Math.Max(1, RefusalWindowSec));
        bucket.Refusals.RemoveAll(t => now - t > window);
        bucket.Refusals.Add(now);

        if (bucket.Refusals.Count >= Math.Max(1, RefusalsBeforeBlock))
        {
            bucket.BlockedUntilUtc = now.AddSeconds(Math.Max(1, BlockDurationSec));
            bucket.Refusals.Clear();
        }
    }

    private void SweepIfDueLocked(DateTime now)
    {
        if (now - _lastSweepUtc < TimeSpan.FromSeconds(30))
            return;
        _lastSweepUtc = now;

        var idle = TimeSpan.FromSeconds(Math.Max(60, IdleEvictionSec));
        var dead = _buckets
            .Where(kv => now - kv.Value.LastSeenUtc > idle
                         && (kv.Value.BlockedUntilUtc is null || now >= kv.Value.BlockedUntilUtc))
            .Select(kv => kv.Key)
            .ToList();
        foreach (var key in dead)
            _buckets.Remove(key);

        // Верхняя граница памяти: адресов не должно накопиться больше, чем нужно городу.
        if (_buckets.Count > 10_000)
        {
            foreach (var key in _buckets
                         .OrderBy(kv => kv.Value.LastSeenUtc)
                         .Take(_buckets.Count - 5_000)
                         .Select(kv => kv.Key)
                         .ToList())
                _buckets.Remove(key);
        }
    }
}

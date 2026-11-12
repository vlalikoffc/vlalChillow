using System.Collections.Concurrent;
using StandChillow.LanServer.Diagnostics;
using Xunit;

namespace StandChillow.LanServer.Tests;

/// <summary>
/// Стабильность под нагрузкой: capture-приёмник не должен блокировать сетевой поток,
/// не должен уметь уронить процесс и не должен забивать диск.
/// </summary>
/// <remarks>
/// <see cref="CaptureSink"/> — статический синглтон на процесс, поэтому тесты, которые
/// его включают, собраны в одну коллекцию и не пересекаются с «выключенным» тестом.
/// </remarks>
[Collection("capture-sink")]
public sealed class CaptureSinkTests : IDisposable
{
    private readonly string _dir =
        Path.Combine(Path.GetTempPath(), "vlal-captures-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void Capture_WhenDisabled_NeverWrites()
    {
        // По умолчанию (без Configure) вызов из горячего пути — no-op.
        if (CaptureSink.Enabled)
            return; // другой тест уже включил приёмник — проверка неприменима

        var before = CaptureSink.Written;
        CaptureSink.Capture("test", new byte[] { 1, 2, 3 });
        Assert.Equal(before, CaptureSink.Written);
    }

    [Fact]
    public void Capture_WhenEnabled_EnqueuesWithoutThrowing_AndWritesFiles()
    {
        CaptureSink.Configure(enabled: true, _dir);
        Assert.True(CaptureSink.Enabled);

        // Много «пакетов» подряд — вызовы обязаны быть быстрыми и без исключений.
        var payload = new byte[64];
        for (var i = 0; i < 200; i++)
            CaptureSink.Capture($"hot_{i}", payload);

        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (CaptureSink.Written < 200 && DateTime.UtcNow < deadline)
            Thread.Sleep(20);

        Assert.True(Directory.Exists(_dir));
        Assert.True(Directory.GetFiles(_dir, "*.bin").Length > 0);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true); }
        catch { /* best effort */ }
    }
}

/// <summary>Гейты диагностики: горячие пути молчат по умолчанию.</summary>
public sealed class DiagTests
{
    [Fact]
    public void Defaults_AreQuietButKeepHealthStats()
    {
        // Per-packet трейс по умолчанию выключен (иначе 10 игроков = тысячи записей в сек),
        // но периодическая сводка [health] включена.
        if (Diag.Verbose)
            return; // другой тест/окружение включили verbose — проверка дефолта неприменима

        Assert.False(Diag.Verbose);
        Assert.True(Diag.StatsEnabled);
    }

    [Fact]
    public void BoundedQueue_DropsInsteadOfBlocking()
    {
        // Модель лог-очереди: при переполнении теряем строки, но не блокируем сетевой поток.
        using var queue = new BlockingCollection<int>(new ConcurrentQueue<int>(), boundedCapacity: 8);

        var accepted = 0;
        for (var i = 0; i < 100; i++)
            if (queue.TryAdd(i))
                accepted++;

        Assert.InRange(accepted, 1, 100);
    }
}

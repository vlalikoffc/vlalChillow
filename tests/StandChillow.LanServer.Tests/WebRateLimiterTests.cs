using System.Net;
using StandChillow.LanServer.Web;
using Xunit;

namespace StandChillow.LanServer.Tests;

/// <summary>
/// Лимиты панели (PR 6 плана): частота, пауза за упорство, изоляция адресов.
/// Время подменено — тесты не спят.
/// </summary>
public sealed class WebRateLimiterTests
{
    private sealed class Clock
    {
        public DateTime Now { get; set; } =
            new(2026, 11, 14, 12, 0, 0, DateTimeKind.Utc);

        public void Advance(int seconds) => Now = Now.AddSeconds(seconds);
    }

    private static readonly IPAddress A = IPAddress.Parse("203.0.113.1");
    private static readonly IPAddress B = IPAddress.Parse("203.0.113.2");

    private static WebRateLimiter NewLimiter(Clock clock) => new(() => clock.Now)
    {
        RequestsPerSecond = 10,
        RefusalsBeforeBlock = 5,
        RefusalWindowSec = 10,
        BlockDurationSec = 60,
    };

    [Fact]
    public void AllowsUpToLimitPerSecond()
    {
        var clock = new Clock();
        var limiter = NewLimiter(clock);

        for (var i = 0; i < 10; i++)
            Assert.True(limiter.Check(A).Allowed);
    }

    [Fact]
    public void DeniesEleventhRequestInSameSecond()
    {
        var clock = new Clock();
        var limiter = NewLimiter(clock);
        for (var i = 0; i < 10; i++)
            limiter.Check(A);

        var decision = limiter.Check(A);

        Assert.False(decision.Allowed);
        Assert.Equal(1, decision.RetryAfterSec);
        Assert.Equal("rate", decision.Reason);
    }

    [Fact]
    public void LimitRecoversNextSecond()
    {
        var clock = new Clock();
        var limiter = NewLimiter(clock);
        for (var i = 0; i < 10; i++)
            limiter.Check(A);
        Assert.False(limiter.Check(A).Allowed);

        clock.Advance(1);

        Assert.True(limiter.Check(A).Allowed);
    }

    [Fact]
    public void FiveRefusalsTriggerBlock()
    {
        var clock = new Clock();
        var limiter = NewLimiter(clock);

        // Пять «лишних» запросов подряд — каждый следующий отказ копит счётчик.
        for (var i = 0; i < 10; i++)
            limiter.Check(A);

        WebRateLimiter.Decision last = default;
        for (var i = 0; i < 5; i++)
            last = limiter.Check(A);

        Assert.False(last.Allowed);

        // Теперь даже через секунду — пауза.
        clock.Advance(1);
        var blocked = limiter.Check(A);
        Assert.False(blocked.Allowed);
        Assert.Equal("blocked", blocked.Reason);
        Assert.True(blocked.RetryAfterSec > 1);
    }

    [Fact]
    public void BlockExpiresAfterItsDuration()
    {
        var clock = new Clock();
        var limiter = NewLimiter(clock);

        for (var i = 0; i < 10; i++)
            limiter.Check(A);
        for (var i = 0; i < 5; i++)
            limiter.Check(A);
        Assert.Equal("blocked", limiter.Check(A).Reason);

        clock.Advance(61);

        Assert.True(limiter.Check(A).Allowed);
    }

    [Fact]
    public void RefusalsOutsideWindowDoNotAccumulate()
    {
        var clock = new Clock();
        var limiter = NewLimiter(clock);

        // Каждую секунду по одному лишнему запросу: окно 10 с чистит старые.
        for (var i = 0; i < 30; i++)
        {
            for (var j = 0; j < 10; j++)
                limiter.Check(A);
            limiter.Check(A); // отказ
            clock.Advance(11); // уходим за окно отказов
        }

        // Никакого «накопленного штрафа» — следующий запрос проходит.
        var decision = limiter.Check(A);
        Assert.True(decision.Allowed);
        Assert.NotEqual("blocked", decision.Reason);
    }

    [Fact]
    public void AddressesAreIndependent()
    {
        var clock = new Clock();
        var limiter = NewLimiter(clock);
        for (var i = 0; i < 11; i++)
            limiter.Check(A);
        Assert.False(limiter.Check(A).Allowed);

        Assert.True(limiter.Check(B).Allowed);
    }

    [Fact]
    public void SuccessfulRequestResetsRefusalStreak()
    {
        var clock = new Clock();
        var limiter = NewLimiter(clock);

        // 4 отказа: 10 проходов + 4 лишних.
        for (var i = 0; i < 14; i++)
            limiter.Check(A);

        clock.Advance(1);
        Assert.True(limiter.Check(A).Allowed); // сброс серии

        for (var i = 0; i < 10; i++)
            limiter.Check(A);

        // После сброса нужно снова 5 отказов, а не 1.
        Assert.Equal("rate", limiter.Check(A).Reason);
    }

    [Fact]
    public void NoteRefusalCountsTowardBlock()
    {
        var clock = new Clock();
        var limiter = NewLimiter(clock);

        for (var i = 0; i < 5; i++)
            limiter.NoteRefusal(A);

        Assert.Equal("blocked", limiter.Check(A).Reason);
    }

    [Fact]
    public void NullAddressIsAlwaysAllowed()
    {
        var clock = new Clock();
        var limiter = NewLimiter(clock);

        for (var i = 0; i < 100; i++)
            Assert.True(limiter.Check(null).Allowed);
    }

    [Fact]
    public void IdleAddressesAreEvicted()
    {
        var clock = new Clock();
        var limiter = new WebRateLimiter(() => clock.Now) { IdleEvictionSec = 60 };

        limiter.Check(A);
        Assert.Equal(1, limiter.TrackedCount);

        clock.Advance(120);
        limiter.Check(B);

        Assert.Equal(1, limiter.TrackedCount);
    }

    [Fact]
    public void ParallelChecksDoNotExceedLimit()
    {
        var clock = new Clock();
        var limiter = NewLimiter(clock);

        var allowed = 0;
        Parallel.For(0, 200, _ =>
        {
            if (limiter.Check(A).Allowed)
                Interlocked.Increment(ref allowed);
        });

        Assert.Equal(10, allowed);
    }
}

using StandChillow.LanServer.Net;
using Xunit;

namespace StandChillow.LanServer.Tests;

/// <summary>
/// PR 4 плана: какой адрес сервер называет игроку, если комнаты нет.
/// Настоящий домен/IP в коде и тестах не участвует — только <c>example.com</c>.
/// </summary>
public sealed class SiteAddressResolverTests
{
    private static SiteAddressResolver With(
        string configured = "",
        string? external = null,
        string? lan = "192.168.1.5") =>
        new()
        {
            ConfiguredBaseUrl = configured,
            ExternalIpProvider = external is null
                ? null
                : _ => Task.FromResult<string?>(external),
            LanAddressProvider = () => lan,
        };

    [Fact]
    public void ConfiguredUrlWinsOverEverything()
    {
        var r = With(configured: "https://example.com/vlalchillow/", external: "203.0.113.9");
        Assert.Equal("https://example.com/vlalchillow/", r.Resolve());
    }

    [Fact]
    public void ConfiguredUrlIsTrimmed()
    {
        Assert.Equal("https://example.com", With(configured: "  https://example.com  ").Resolve());
    }

    [Fact]
    public void ExternalIpUsedWhenNotLocal()
    {
        Assert.Equal("203.0.113.9", With(external: "203.0.113.9").Resolve());
    }

    [Fact]
    public void LocalExternalAddressIsIgnored()
    {
        // За NAT сервис может вернуть приватный адрес — показывать его игроку нельзя.
        var r = With(external: "192.168.1.5", lan: "10.0.0.7");
        Assert.Equal("10.0.0.7", r.Resolve());
    }

    [Fact]
    public void LanUsedWhenNoExternalProvider()
    {
        Assert.Equal("192.168.1.5", With().Resolve());
    }

    [Fact]
    public void LocalhostWhenNothingKnown()
    {
        var r = new SiteAddressResolver { LanAddressProvider = () => null };
        Assert.Equal("localhost", r.Resolve());
    }

    [Fact]
    public void ExplicitLanProviderBlocksRealInterfaces()
    {
        // Заданный делегат — окончательный ответ: «LAN-адреса нет» не значит
        // «спроси настоящие интерфейсы». Иначе тест зависел бы от машины,
        // на которой он запущен (на CI раннере LAN-адрес есть — 10.x).
        var called = false;
        var r = new SiteAddressResolver
        {
            LanAddressProvider = () => { called = true; return null; },
        };

        Assert.Equal("localhost", r.Resolve());
        Assert.True(called);
    }

    [Fact]
    public void MissingLanProviderFallsBackToInterfaces()
    {
        // Провайдер не задан — поведение по умолчанию: взять LAN-адрес машины.
        // Проверяем только что ответ непустой (конкретный адрес зависит от машины).
        var r = new SiteAddressResolver();
        Assert.False(string.IsNullOrWhiteSpace(r.Resolve()));
    }

    [Fact]
    public void ProviderFailureFallsBackToLan()
    {
        var r = new SiteAddressResolver
        {
            ExternalIpProvider = _ => throw new HttpRequestException("нет сети"),
            LanAddressProvider = () => "198.51.100.9",
        };
        Assert.Equal("198.51.100.9", r.Resolve());
    }

    [Fact]
    public void ExternalIpIsCached()
    {
        var calls = 0;
        var clock = DateTime.UtcNow;
        var r = new SiteAddressResolver(() => clock)
        {
            ExternalIpProvider = _ =>
            {
                calls++;
                return Task.FromResult<string?>("203.0.113.9");
            },
            LanAddressProvider = () => "10.0.0.1",
        };

        Assert.Equal("203.0.113.9", r.Resolve());
        Assert.Equal("203.0.113.9", r.Resolve());
        Assert.Equal(1, calls);
    }

    [Fact]
    public void CacheExpiresAfterTtl()
    {
        var calls = 0;
        var clock = new DateTime(2026, 11, 14, 12, 0, 0, DateTimeKind.Utc);
        var r = new SiteAddressResolver(() => clock)
        {
            ExternalIpProvider = _ =>
            {
                calls++;
                return Task.FromResult<string?>("203.0.113.9");
            },
            LanAddressProvider = () => "10.0.0.1",
        };

        r.Resolve();
        clock = clock.Add(SiteAddressResolver.ExternalIpCacheTtl + TimeSpan.FromSeconds(1));
        r.Resolve();

        Assert.Equal(2, calls);
    }

    [Fact]
    public void InvalidateForcesRefresh()
    {
        var calls = 0;
        var r = new SiteAddressResolver
        {
            ExternalIpProvider = _ =>
            {
                calls++;
                return Task.FromResult<string?>("203.0.113.9");
            },
            LanAddressProvider = () => "10.0.0.1",
        };

        r.Resolve();
        r.Invalidate();
        r.Resolve();

        Assert.Equal(2, calls);
    }

    [Fact]
    public void FailedLookupIsNotRepeatedImmediately()
    {
        var calls = 0;
        var r = new SiteAddressResolver
        {
            ExternalIpProvider = _ =>
            {
                calls++;
                return Task.FromResult<string?>(null);
            },
            LanAddressProvider = () => "10.0.0.1",
        };

        r.Resolve();
        r.Resolve();
        r.Resolve();

        Assert.Equal(1, calls);
    }

    [Theory]
    [InlineData("203.0.113.9", "203.0.113.9")]
    [InlineData("  203.0.113.9\n", "203.0.113.9")]
    [InlineData("\"203.0.113.9\"", "203.0.113.9")]
    [InlineData("example.com", "example.com")]
    [InlineData("203.0.113.9:8080", "203.0.113.9:8080")]
    public void NormalizesHostValues(string raw, string expected)
    {
        Assert.Equal(expected, SiteAddressResolver.NormalizeHostValue(raw));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    [InlineData("<html>error</html>")]
    [InlineData("нет сети")]
    [InlineData("203.0.113.9\n203.0.114.1")]
    public void RejectsJunkHostValues(string? raw)
    {
        Assert.Null(SiteAddressResolver.NormalizeHostValue(raw));
    }

    [Theory]
    [InlineData("10.0.0.1", true)]
    [InlineData("192.168.1.1", true)]
    [InlineData("172.16.0.1", true)]
    [InlineData("172.31.255.255", true)]
    [InlineData("127.0.0.1", true)]
    [InlineData("169.254.1.1", true)]
    [InlineData("172.32.0.1", false)]
    [InlineData("203.0.113.9", false)]
    [InlineData("8.8.8.8", false)]
    public void DetectsLocalAddresses(string host, bool local)
    {
        Assert.Equal(local, SiteAddressResolver.IsLocalAddress(host));
    }

    [Fact]
    public void BareHostnameIsNotLocal()
    {
        Assert.False(SiteAddressResolver.IsLocalAddress("example.com"));
    }
}

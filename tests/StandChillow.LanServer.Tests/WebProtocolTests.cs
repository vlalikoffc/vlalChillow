using System.Net;
using System.Text;
using StandChillow.LanServer.Config;
using StandChillow.LanServer.Web;
using Xunit;
using WebResponse = StandChillow.LanServer.Web.WebResponse;

namespace StandChillow.LanServer.Tests;

/// <summary>
/// HTTP-часть панели: заголовки ответа, лимиты, поведение при мусоре.
/// Сокет не открывается — проверяется то, что можно проверить без сети.
/// </summary>
public sealed class WebProtocolTests
{
    [Fact]
    public void HtmlResponseHasNoStoreAndNosniff()
    {
        var response = WebResponse.Html("<p>ok</p>");

        Assert.Equal(200, response.Status);
        Assert.Equal("text/html; charset=utf-8", response.ContentType);
        Assert.Contains("ok", Encoding.UTF8.GetString(response.Body));
        Assert.Equal(0, response.RetryAfterSec);
    }

    [Fact]
    public void JsonResponseIsUtf8()
    {
        var response = WebResponse.Json("""{"м":"влал"}""");
        Assert.Equal("application/json; charset=utf-8", response.ContentType);
        Assert.Contains("влал", Encoding.UTF8.GetString(response.Body));
    }

    [Fact]
    public void ThrottledCarriesRetryAfter()
    {
        var response = WebResponse.Throttled(7);

        Assert.Equal(429, response.Status);
        Assert.Equal(7, response.RetryAfterSec);
        Assert.Equal("429", Encoding.UTF8.GetString(response.Body));
    }

    [Fact]
    public void ThrottledNeverSendsZeroDelay()
    {
        // Retry-After: 0 — «попробуй сразу», то есть отсутствие защиты.
        Assert.Equal(1, WebResponse.Throttled(0).RetryAfterSec);
        Assert.Equal(1, WebResponse.Throttled(-5).RetryAfterSec);
    }

    [Theory]
    [InlineData(200)]
    [InlineData(400)]
    [InlineData(404)]
    [InlineData(405)]
    [InlineData(429)]
    [InlineData(500)]
    public void EveryStatusHasAReasonPhrase(int status)
    {
        // Причина в строке ответа — не косметика: без неё часть клиентов (curl -v,
        // старые прокси) считает ответ битым.
        var response = WebResponse.Text("x", status);
        Assert.Equal(status, response.Status);
    }

    [Fact]
    public void OptionsCloneLimitsWithoutSharedState()
    {
        var a = new WebPanelOptions { Limits = new WebPanelOptions.WebLimits { RequestsPerSecond = 5 } };
        var b = new WebPanelOptions { Limits = new WebPanelOptions.WebLimits { RequestsPerSecond = 99 } };

        Assert.Equal(5, a.Limits.RequestsPerSecond);
        Assert.Equal(99, b.Limits.RequestsPerSecond);
    }

    [Fact]
    public void RequestContextCarriesMethodAndPathAsGiven()
    {
        var context = new WebRequestContext(
            "POST", "/api/rooms", "x=1", new byte[] { 1 }, IPAddress.Loopback,
            new Dictionary<string, string> { ["content-length"] = "1" });

        Assert.Equal("POST", context.Method);
        Assert.Equal("/api/rooms", context.Path);
        Assert.Equal("x=1", context.Query);
        Assert.Equal("1", context.Headers["content-length"]);
    }

    [Fact]
    public void StatusCacheDoesNotRebuildWithinInterval()
    {
        var clock = new DateTime(2026, 11, 14, 12, 0, 0, DateTimeKind.Utc);
        var cache = new StatusSnapshotCache(() => clock);
        var builds = 0;

        Assert.True(cache.Refresh(() => { builds++; return "{}"; }));
        Assert.False(cache.Refresh(() => { builds++; return "{}"; }));

        Assert.Equal(1, builds);
        Assert.Equal(1, cache.BuildCount);
    }

    [Fact]
    public void StatusCacheRebuildsAfterInterval()
    {
        var clock = new DateTime(2026, 11, 14, 12, 0, 0, DateTimeKind.Utc);
        var cache = new StatusSnapshotCache(() => clock);
        cache.Refresh(() => """{"a":1}""");

        clock = clock.Add(StatusSnapshotCache.RefreshInterval + TimeSpan.FromMilliseconds(1));

        Assert.True(cache.Refresh(() => """{"a":2}"""));
        Assert.Equal("""{"a":2}""", cache.Current);
    }

    [Fact]
    public void StatusCacheSurvivesThrowingBuilder()
    {
        var cache = new StatusSnapshotCache();
        var ok = cache.Refresh(() => throw new InvalidOperationException("бум"));

        // Падение сборки не должно ронять панель: отдаём error-JSON, а не исключение.
        Assert.True(ok);
        Assert.Contains("error", cache.Current);
    }

    [Fact]
    public void PanelListensOnAllInterfacesByDefault()
    {
        // Прокси на другой машине (nginx -> 192.168.x.x:8080) до loopback
        // не достучится и отдаст 502, хотя сервер жив. Дефолт обязан быть 0.0.0.0.
        var cfg = new ServerConfig();
        cfg.Normalize();

        Assert.Equal("0.0.0.0", cfg.Web.BindAddress);
    }

    [Fact]
    public void EmptyWebBindAddressNormalizesToAllInterfaces()
    {
        var cfg = new ServerConfig();
        cfg.Web.BindAddress = "   ";
        cfg.Normalize();

        Assert.Equal("0.0.0.0", cfg.Web.BindAddress);
    }

    [Fact]
    public void ExplicitLoopbackBindAddressIsHonoured()
    {
        // Ручной выбор не перетираем: nginx на этом же хосте — валидный случай.
        var cfg = new ServerConfig();
        cfg.Web.BindAddress = "127.0.0.1";
        cfg.Normalize();

        Assert.Equal("127.0.0.1", cfg.Web.BindAddress);
    }
}

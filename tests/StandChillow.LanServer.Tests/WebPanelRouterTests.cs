using System.Net;
using System.Text;
using StandChillow.LanServer.Net.Match.Kernel;
using StandChillow.LanServer.Web;
using Xunit;
using WebResponse = StandChillow.LanServer.Web.WebResponse;

namespace StandChillow.LanServer.Tests;

/// <summary>
/// PR 6 плана: маршруты панели. Сокетов нет — проверяются статусы, JSON и правила
/// реестра, то есть всё, что раньше проверялось бы «глазами в браузере».
/// </summary>
public sealed class WebPanelRouterTests
{
    private static readonly string IndexHtml = "<!doctype html><title>тест</title>";

    private static WebPanelRouter NewRouter(
        out RoomRegistry rooms,
        Func<RoomDescriptor, string?>? onCreated = null,
        string basePath = "")
    {
        rooms = new RoomRegistry();
        return new WebPanelRouter(
            rooms,
            () => IndexHtml,
            new StatusSnapshotCache(),
            new WebPanelRouterOptions
            {
                BasePath = basePath,
                OnRoomCreated = onCreated,
                RequireOwnerNick = true,
            });
    }

    private static WebRequestContext Get(string path) => new(
        "GET", path, "", Array.Empty<byte>(), IPAddress.Parse("203.0.113.1"),
        new Dictionary<string, string>());

    private static WebRequestContext Post(string path, string json) => new(
        "POST", path, "", Encoding.UTF8.GetBytes(json), IPAddress.Parse("203.0.113.1"),
        new Dictionary<string, string>());

    private static string Body(WebResponse response) =>
        Encoding.UTF8.GetString(response.Body);

    [Fact]
    public void RootServesEmbeddedPage()
    {
        var router = NewRouter(out _);
        var response = router.Route(Get("/"));

        Assert.Equal(200, response.Status);
        Assert.Contains("text/html", response.ContentType);
        Assert.Equal(IndexHtml, Body(response));
    }

    [Fact]
    public void IndexHtmlIsAlsoServed()
    {
        var router = NewRouter(out _);
        Assert.Equal(200, router.Route(Get("/index.html")).Status);
    }

    [Fact]
    public void UnknownPathIs404()
    {
        var router = NewRouter(out _);
        Assert.Equal(404, router.Route(Get("/nope")).Status);
    }

    [Fact]
    public void HealthEndpointWorks()
    {
        var router = NewRouter(out _);
        var response = router.Route(Get("/healthz"));
        Assert.Equal(200, response.Status);
        Assert.Equal("ok", Body(response));
    }

    [Fact]
    public void RoomsStartsEmptyWithNextNumberOne()
    {
        var router = NewRouter(out _);
        var json = Body(router.Route(Get("/api/rooms")));

        Assert.Contains("\"nextNumber\":1", json);
        Assert.Contains("\"count\":0", json);
    }

    [Fact]
    public void CreatingRoomReturns201AndJoinCommand()
    {
        var router = NewRouter(out var rooms);
        var response = router.Route(Post("/api/rooms",
            """{"mode":"2v2","level":"Sandstone","name":"вечерняя","owner":"влал"}"""));

        Assert.Equal(201, response.Status);
        var json = Body(response);
        Assert.Contains("\"number\":1", json);
        Assert.Contains("\"joinCommand\":\"/join 2v2 #1\"", json);
        Assert.Contains("\"state\":\"waiting\"", json);
        Assert.Equal(1, rooms.Count);
    }

    [Fact]
    public void NumbersGrowMonotonically()
    {
        var router = NewRouter(out _);
        router.Route(Post("/api/rooms", """{"mode":"2v2","owner":"a"}"""));
        var second = router.Route(Post("/api/rooms", """{"mode":"duel","owner":"b"}"""));

        Assert.Contains("\"number\":2", Body(second));
        Assert.Contains("\"joinCommand\":\"/join duel #2\"", Body(second));
    }

    [Fact]
    public void SecondRoomForSameOwnerIsConflict()
    {
        var router = NewRouter(out _);
        router.Route(Post("/api/rooms", """{"mode":"2v2","owner":"влал"}"""));

        var dup = router.Route(Post("/api/rooms", """{"mode":"duel","owner":"ВЛАЛ"}"""));

        Assert.Equal(409, dup.Status);
        Assert.Contains("one_room_per_player", Body(dup));
    }

    [Fact]
    public void UnknownModeIs400()
    {
        var router = NewRouter(out _);
        var response = router.Route(Post("/api/rooms", """{"mode":"нетакого","owner":"a"}"""));

        Assert.Equal(400, response.Status);
        Assert.Contains("unknown_mode", Body(response));
    }

    [Fact]
    public void UnknownLevelIs400()
    {
        var router = NewRouter(out _);
        var response = router.Route(Post("/api/rooms",
            """{"mode":"2v2","level":"Block","owner":"a"}"""));

        Assert.Equal(400, response.Status);
        Assert.Contains("unknown_level", Body(response));
    }

    [Fact]
    public void MissingOwnerIs400()
    {
        var router = NewRouter(out _);
        var response = router.Route(Post("/api/rooms", """{"mode":"2v2"}"""));

        Assert.Equal(400, response.Status);
        Assert.Contains("empty_owner", Body(response));
    }

    [Fact]
    public void BrokenJsonIs400()
    {
        var router = NewRouter(out _);
        var response = router.Route(Post("/api/rooms", "{не json"));

        Assert.Equal(400, response.Status);
        Assert.Contains("bad_json", Body(response));
    }

    [Fact]
    public void EmptyBodyIs400()
    {
        var router = NewRouter(out _);
        Assert.Equal(400, router.Route(Post("/api/rooms", "")).Status);
    }

    [Fact]
    public void ModeDefaultsWhenOmitted()
    {
        var router = NewRouter(out _);
        var response = router.Route(Post("/api/rooms", """{"owner":"влал"}"""));
        Assert.Equal(201, response.Status);
    }

    [Fact]
    public void FailedGameRoomCreationRollsBackRegistry()
    {
        var attempts = 0;
        var router = NewRouter(out var rooms, _ => ++attempts == 1 ? "игровой порт не поднялся" : null);

        var response = router.Route(Post("/api/rooms", """{"mode":"2v2","owner":"a"}"""));

        Assert.Equal(500, response.Status);
        Assert.Contains("room_not_created", Body(response));

        // Реестр откатился: комнаты нет.
        Assert.Equal(0, rooms.Count);

        // Номер освободился вместе с откатом: следующая комната снова берёт
        // наименьший свободный, то есть #1. Комнаты #1 в реестре больше нет.
        var retry = router.Route(Post("/api/rooms", """{"mode":"2v2","owner":"b"}"""));
        Assert.Equal(201, retry.Status);
        Assert.Contains("\"number\":1", Body(retry));
    }

    [Fact]
    public void RoomsListShowsCreatedRoom()
    {
        var router = NewRouter(out _);
        router.Route(Post("/api/rooms",
            """{"mode":"tdm","name":"тестовая","owner":"влал"}"""));

        var json = Body(router.Route(Get("/api/rooms")));

        Assert.Contains("\"count\":1", json);
        Assert.Contains("\"owner\":\"влал\"", json);
        Assert.Contains("\"name\":\"тестовая\"", json);
    }

    [Fact]
    public void WrongMethodOnRoomsIs405WithAllow()
    {
        var router = NewRouter(out _);
        var response = router.Route(new WebRequestContext(
            "DELETE", "/api/rooms", "", Array.Empty<byte>(), null,
            new Dictionary<string, string>()));

        Assert.Equal(405, response.Status);
        Assert.Contains(response.ExtraHeaders, h => h.Item1 == "Allow" && h.Item2 == "GET, POST");
    }

    [Fact]
    public void WrongMethodOnRootIs405()
    {
        var router = NewRouter(out _);
        var response = router.Route(new WebRequestContext(
            "POST", "/", "", Array.Empty<byte>(), null,
            new Dictionary<string, string>()));

        Assert.Equal(405, response.Status);
    }

    [Fact]
    public void StatusIsServedFromCache()
    {
        var rooms = new RoomRegistry();
        var cache = new StatusSnapshotCache();
        cache.Set("""{"online":0}""");
        var router = new WebPanelRouter(rooms, () => IndexHtml, cache,
            new WebPanelRouterOptions());

        Assert.Equal("""{"online":0}""", Body(router.Route(Get("/api/status"))));
    }

    [Theory]
    [InlineData("/vlalchillow", "/vlalchillow/", "/")]
    [InlineData("/vlalchillow", "/vlalchillow/api/rooms", "/api/rooms")]
    [InlineData("/vlalchillow/", "/vlalchillow/api/rooms", "/api/rooms")]
    [InlineData("vlalchillow", "/vlalchillow/", "/")]
    [InlineData("", "/api/rooms", "/api/rooms")]
    [InlineData("/vlalchillow", "/api/rooms", "/api/rooms")]
    [InlineData("/vlalchillow", "//vlalchillow//api//rooms", "/api/rooms")]
    public void NormalizesProxyPrefix(string basePath, string path, string expected)
    {
        Assert.Equal(expected, WebPanelRouter.NormalizePath(path, basePath));
    }

    [Fact]
    public void WorksBehindProxyPrefix()
    {
        var router = NewRouter(out _, basePath: "/vlalchillow");
        Assert.Equal(200, router.Route(Get("/vlalchillow/")).Status);
        Assert.Equal(200, router.Route(Get("/vlalchillow/api/rooms")).Status);
    }

    [Fact]
    public void JsonDoesNotEscapeCyrillicIntoAscii()
    {
        var router = NewRouter(out _);
        router.Route(Post("/api/rooms", """{"mode":"2v2","owner":"влал"}"""));

        // Читаемая кириллица в JSON — иначе в отладке сети всё превращается в \uXXXX.
        Assert.Contains("влал", Body(router.Route(Get("/api/rooms"))));
    }
}

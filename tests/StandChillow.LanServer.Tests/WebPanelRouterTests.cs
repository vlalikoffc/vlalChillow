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

                // «В лобби» — те же три игрока, чьи id используют тесты ниже.
                OnlinePlayers = () => new List<(int Id, string Nick)>
                {
                    (1000, "влал"),
                    (1001, "a"),
                    (1002, "b"),
                },
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
            """{"mode":"2v2","level":"Sandstone","name":"вечерняя","ownerId":1000}"""));

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
        router.Route(Post("/api/rooms", """{"mode":"2v2","ownerId":1001}"""));
        var second = router.Route(Post("/api/rooms", """{"mode":"duel","ownerId":1002}"""));

        Assert.Contains("\"number\":2", Body(second));
        Assert.Contains("\"joinCommand\":\"/join duel #2\"", Body(second));
    }

    [Fact]
    public void RoomIsCreatedOnlyForPlayerInLobby()
    {
        var router = NewRouter(out var rooms);

        // 9999 в лобби нет — комната не создаётся, и человеку сказано почему.
        var response = router.Route(Post("/api/rooms", """{"mode":"2v2","ownerId":9999}"""));

        Assert.Equal(409, response.Status);
        Assert.Contains("player_offline", Body(response));
        Assert.Equal(0, rooms.Count);
    }

    [Fact]
    public void NickComesFromServerNotFromRequest()
    {
        // Ник в теле запроса вообще не участвует: даже если прислать чужой,
        // владельцем станет тот, чей id пришёл, и подпись будет его настоящая.
        var router = NewRouter(out _);
        router.Route(Post("/api/rooms",
            """{"mode":"2v2","name":"моя","ownerId":1001,"owner":"подмена"}"""));

        var json = Body(router.Route(Get("/api/rooms")));

        Assert.Contains("\"owner\":\"a\"", json);
        Assert.DoesNotContain("подмена", json);
    }

    [Fact]
    public void MissingOwnerIdIsRejected()
    {
        var router = NewRouter(out var rooms);

        var response = router.Route(Post("/api/rooms", """{"mode":"2v2"}"""));

        Assert.Equal(400, response.Status);
        Assert.Contains("empty_owner", Body(response));
        Assert.Equal(0, rooms.Count);
    }

    [Fact]
    public void NickInsteadOfIdIsRejectedClearly()
    {
        // Ник — не идентификатор: сервер говорит об этом отдельным кодом,
        // а не «пустое поле», иначе человек чинит не ту часть формы.
        var router = NewRouter(out var rooms);

        var response = router.Route(Post("/api/rooms", """{"mode":"2v2","owner":"влал"}"""));

        Assert.Equal(400, response.Status);
        Assert.Contains("nick_not_supported", Body(response));
        Assert.Equal(0, rooms.Count);
    }

    [Fact]
    public void DifferentPlayersGetSeparateRooms()
    {
        // Раньше ключом был ник: двое с одинаковым ником не могли оба создать
        // комнату, а один мог занять «чужую» комнату, вписав чужой ник.
        var router = NewRouter(out var rooms);

        Assert.Equal(201, router.Route(Post("/api/rooms",
            """{"mode":"2v2","ownerId":1001}""")).Status);
        Assert.Equal(201, router.Route(Post("/api/rooms",
            """{"mode":"2v2","ownerId":1002}""")).Status);
        Assert.Equal(2, rooms.Count);
    }

    [Fact]
    public void CreatedRoomReportsItsOwnCapacity()
    {
        // Ответ создания обязан нести потолок: иначе панель рисует 4/16
        // у комнаты «2 на 2», где мест всего четыре.
        var router = NewRouter(out _);

        var response = router.Route(Post("/api/rooms",
            """{"mode":"2v2","ownerId":1001,"perTeam":1}"""));
        var json = Body(response);

        Assert.Equal(201, response.Status);
        Assert.Contains("\"perTeam\":1", json);
        Assert.Contains("\"maxPlayers\":2", json);
        Assert.Contains("\"teamLabel\":\"1 на 1\"", json);
    }

    [Fact]
    public void RoomListCarriesOwnerIdAndCapacity()
    {
        var router = NewRouter(out _);
        router.Route(Post("/api/rooms", """{"mode":"duel","ownerId":1002,"perTeam":2}"""));

        var json = Body(router.Route(Get("/api/rooms")));

        Assert.Contains("\"ownerId\":1002", json);
        Assert.Contains("\"maxPlayers\":4", json);
        Assert.Contains("\"teamLabel\":\"2 на 2\"", json);
    }

    [Fact]
    public void SecondRoomForSameOwnerIsConflict()
    {
        var router = NewRouter(out _);
        router.Route(Post("/api/rooms", """{"mode":"2v2","ownerId":1000}"""));

        var dup = router.Route(Post("/api/rooms", """{"mode":"duel","ownerId":1000}"""));

        Assert.Equal(409, dup.Status);
        Assert.Contains("one_room_per_player", Body(dup));
    }

    [Fact]
    public void UnknownModeIs400()
    {
        var router = NewRouter(out _);
        var response = router.Route(Post("/api/rooms", """{"mode":"нетакого","ownerId":1001}"""));

        Assert.Equal(400, response.Status);
        Assert.Contains("unknown_mode", Body(response));
    }

    [Fact]
    public void UnknownLevelIs400()
    {
        var router = NewRouter(out _);
        var response = router.Route(Post("/api/rooms",
            """{"mode":"2v2","level":"Block","ownerId":1001}"""));

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
        var response = router.Route(Post("/api/rooms", """{"ownerId":1000}"""));
        Assert.Equal(201, response.Status);
    }

    [Fact]
    public void FailedGameRoomCreationRollsBackRegistry()
    {
        var attempts = 0;
        var router = NewRouter(out var rooms, _ => ++attempts == 1 ? "игровой порт не поднялся" : null);

        var response = router.Route(Post("/api/rooms", """{"mode":"2v2","ownerId":1001}"""));

        Assert.Equal(500, response.Status);
        Assert.Contains("room_not_created", Body(response));

        // Реестр откатился: комнаты нет.
        Assert.Equal(0, rooms.Count);

        // Номер освободился вместе с откатом: следующая комната снова берёт
        // наименьший свободный, то есть #1. Комнаты #1 в реестре больше нет.
        var retry = router.Route(Post("/api/rooms", """{"mode":"2v2","ownerId":1002}"""));
        Assert.Equal(201, retry.Status);
        Assert.Contains("\"number\":1", Body(retry));
    }

    [Fact]
    public void RoomsListShowsCreatedRoom()
    {
        var router = NewRouter(out _);
        router.Route(Post("/api/rooms",
            """{"mode":"tdm","name":"тестовая","ownerId":1000}"""));

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
        router.Route(Post("/api/rooms", """{"mode":"2v2","ownerId":1000}"""));

        // Читаемая кириллица в JSON — иначе в отладке сети всё превращается в \uXXXX.
        Assert.Contains("влал", Body(router.Route(Get("/api/rooms"))));
    }
}

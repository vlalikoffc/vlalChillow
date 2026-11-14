using System.Net;
using System.Text;
using System.Text.Json;
using StandChillow.LanServer.Config;
using StandChillow.LanServer.Net.Lobby;
using StandChillow.LanServer.Net.Match.Kernel;

namespace StandChillow.LanServer.Web;

/// <summary>
/// Маршруты панели и единственное место, где рождается JSON.
/// </summary>
/// <remarks>
/// <para>
/// Маршрутов ровно четыре: страница, сводка, список комнат и создание комнаты. Никакой
/// авторизации: спека требует «одна кнопка», поэтому защита — только лимиты запросов и
/// правила самого реестра (одна комната на ник, номер не переиспользуется).
/// </para>
/// <para>
/// Создание комнаты — <b>единственный</b> путь появления комнаты: команды
/// <c>/create</c> в лобби нет намеренно, комнаты живут только с сайта.
/// </para>
/// <para>
/// Роутер не знает о сокетах: ему дают функцию «создать игровую комнату» и реестр.
/// Поэтому все ветки проверяются тестами без сети.
/// </para>
/// </remarks>
public sealed class WebPanelRouter
{
    private readonly RoomRegistry _rooms;
    private readonly Func<string> _indexHtml;
    private readonly StatusSnapshotCache _status;
    private readonly WebPanelRouterOptions _options;

    public WebPanelRouter(
        RoomRegistry rooms,
        Func<string> indexHtml,
        StatusSnapshotCache status,
        WebPanelRouterOptions options)
    {
        _rooms = rooms;
        _indexHtml = indexHtml;
        _status = status;
        _options = options;
    }

    /// <summary>Разобрать запрос и вернуть ответ. Никогда не бросает.</summary>
    public WebResponse Route(WebRequestContext request)
    {
        var path = NormalizePath(request.Path, _options.BasePath);

        // Страница и её данные — только чтение, доступны всем.
        if (request.Method == "GET" && (path == "/" || path == "/index.html"))
            return WebResponse.Html(_indexHtml());

        if (request.Method == "GET" && path == "/api/status")
            return WebResponse.Json(_status.Current);

        if (request.Method == "GET" && path == "/api/rooms")
            return WebResponse.Json(BuildRoomsJson());

        // Каталог режимов и карт отдаёт сервер, а не страница: иначе копия в JS
        // разъедется с GameModeCatalog, и панель начнёт предлагать несуществующие карты.
        if (request.Method == "GET" && path == "/api/modes")
            return WebResponse.Json(BuildModesJson());

        if (request.Method == "POST" && path == "/api/rooms")
            return CreateRoom(request.Body);

        if (request.Method == "GET" && path == "/healthz")
            return WebResponse.Text("ok");

        // Метод не тот — это не «нет маршрута», и панель должна сказать честно.
        if (path is "/" or "/index.html" or "/api/status" or "/api/rooms"
            or "/api/modes" or "/healthz")
            return WithAllow(path == "/api/rooms" ? "GET, POST" : "GET", WebResponse.Text("405", 405));

        return WebResponse.Text("404", status: 404);
    }

    /// <summary>
    /// Срезать префикс подпути, если панель стоит за прокси (<c>/vlalchillow/api/rooms</c>).
    /// </summary>
    /// <remarks>
    /// Прокси со слэшем в <c>proxy_pass</c> обычно и так срезает префикс, но полагаться на
    /// это нельзя: конфиг nginx правит владелец сервера, а панель должна работать и с
    /// префиксом, и без него.
    /// </remarks>
    public static string NormalizePath(string path, string? basePath)
    {
        var result = string.IsNullOrEmpty(path) ? "/" : path;

        // Склеиваем слэши ДО среза префикса: прокси иногда отдаёт «//vlalchillow//api»,
        // и тогда префикс не совпал бы ни с чем.
        while (result.Contains("//", StringComparison.Ordinal))
            result = result.Replace("//", "/", StringComparison.Ordinal);

        if (!string.IsNullOrWhiteSpace(basePath))
        {
            var prefix = basePath.Trim();
            if (!prefix.StartsWith('/'))
                prefix = "/" + prefix;
            prefix = prefix.TrimEnd('/');

            if (prefix.Length > 0)
            {
                if (result.Equals(prefix, StringComparison.Ordinal))
                    result = "/";
                else if (result.StartsWith(prefix + "/", StringComparison.Ordinal))
                    result = result[prefix.Length..];
            }
        }

        var queryAt = result.IndexOf('?');
        if (queryAt >= 0)
            result = result[..queryAt];

        return result.Length == 0 ? "/" : result;
    }

    /// <summary>
    /// Режимы с их картами и алиасами — из <see cref="GameModeCatalog"/>, один в один.
    /// </summary>
    /// <remarks>
    /// Отдаём и <c>alias</c> (то, что вставляется в <c>/join</c>), и <c>modeId</c>
    /// (то, что понимает сервер): странице не нужно ничего додумывать.
    /// </remarks>
    private static string BuildModesJson()
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, JsonText.WriterOptions))
        {
            writer.WriteStartObject();
            writer.WriteStartArray("modes");
            foreach (var mode in GameModeCatalog.Modes)
            {
                writer.WriteStartObject();
                writer.WriteString("id", mode.GameModeId);
                writer.WriteString("name", mode.DisplayName);
                writer.WriteString("alias", GameModeCatalog.PrimaryAlias(mode));
                writer.WriteString("defaultLevel", GameModeCatalog.DefaultLevelFor(mode.GameModeId));
                writer.WriteStartArray("levels");
                foreach (var level in mode.Levels)
                    writer.WriteStringValue(level);
                writer.WriteEndArray();
                writer.WriteStartArray("aliases");
                foreach (var alias in mode.Aliases)
                    writer.WriteStringValue(alias);
                writer.WriteEndArray();
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private string BuildRoomsJson()
    {
        var rooms = _rooms.Snapshot();
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, JsonText.WriterOptions))
        {
            writer.WriteStartObject();
            writer.WriteNumber("nextNumber", _rooms.NextNumber);
            writer.WriteNumber("count", rooms.Count);
            writer.WriteNumber("maxRooms", _rooms.MaxRooms);
            writer.WriteStartArray("rooms");
            foreach (var room in rooms)
            {
                writer.WriteStartObject();
                writer.WriteNumber("number", room.Number);
                writer.WriteString("mode", room.ModeId);
                writer.WriteString("modeName", DisplayModeName(room.ModeId));
                writer.WriteString("level", room.Level);
                writer.WriteString("name", room.Name);
                writer.WriteString("owner", room.OwnerNick);
                writer.WriteString("state", room.State.ToString().ToLowerInvariant());
                writer.WriteNumber("players", room.Players);
                writer.WriteString("joinCommand", FormatJoinCommand(room));
                writer.WriteString("createdUtc", room.CreatedUtc.ToString("O"));
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    /// <summary>Команда, которую панель даёт скопировать: ровно то, что понимает лобби.</summary>
    public static string FormatJoinCommand(RoomDescriptor room) =>
        $"/join {PrimaryModeToken(room.ModeId)} #{room.Number}";

    private static string PrimaryModeToken(string modeId) =>
        GameModeCatalog.TryGetMode(modeId, out var mode)
            ? GameModeCatalog.PrimaryAlias(mode)
            : modeId;

    private static string DisplayModeName(string modeId) =>
        GameModeCatalog.TryGetMode(modeId, out var mode) ? mode.DisplayName : modeId;

    private WebResponse CreateRoom(byte[] body)
    {
        CreateRoomRequest? parsed;
        try
        {
            parsed = JsonSerializer.Deserialize<CreateRoomRequest>(body, JsonOptions);
        }
        catch (JsonException)
        {
            return ApiError(400, "bad_json", "тело запроса — не JSON");
        }

        if (parsed is null)
            return ApiError(400, "bad_json", "пустое тело запроса");

        // Одна кнопка: владелец — тот, кто создаёт. Ник приходит из лобби, а не из пароля.
        if (!_options.RequireOwnerNick && string.IsNullOrWhiteSpace(parsed.Owner))
            return ApiError(400, "empty_owner", "нет ника владельца");

        var modeToken = string.IsNullOrWhiteSpace(parsed.Mode)
            ? _options.DefaultModeId
            : parsed.Mode;

        if (!_rooms.TryCreate(
                modeToken, parsed.Level, parsed.Name, parsed.Owner, out var room, out var error)
            || room is null)
        {
            var (status, code, message) = MapCreateError(error);
            return ApiError(status, code, message);
        }
        var created = _options.OnRoomCreated?.Invoke(room);
        if (created is { Length: > 0 })
        {
            // Комнату в реестре откатить нельзя «наполовину»: если игровая комната не
            // поднялась, честнее удалить запись, чем показывать кнопку в мёртвую комнату.
            _rooms.TryRemove(room.Number, out _);
            return ApiError(500, "room_not_created", created);
        }

        var json = BuildCreatedJson(room);
        return WebResponse.Json(json, 201);
    }

    private string BuildCreatedJson(RoomDescriptor room)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, JsonText.WriterOptions))
        {
            writer.WriteStartObject();
            writer.WriteNumber("number", room.Number);
            writer.WriteString("mode", room.ModeId);
            writer.WriteString("modeName", DisplayModeName(room.ModeId));
            writer.WriteString("level", room.Level);
            writer.WriteString("name", room.Name);
            writer.WriteString("owner", room.OwnerNick);
            writer.WriteString("joinCommand", FormatJoinCommand(room));
            writer.WriteString("state", room.State.ToString().ToLowerInvariant());
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static (int Status, string Code, string Message) MapCreateError(RoomCreateError error) =>
        error switch
        {
            RoomCreateError.UnknownMode => (400, "unknown_mode", "неизвестный режим"),
            RoomCreateError.UnknownLevel => (400, "unknown_level", "такой карты у режима нет"),
            RoomCreateError.EmptyNick => (400, "empty_owner", "нет ника владельца"),
            RoomCreateError.DuplicateNick => (409, "one_room_per_player",
                "у этого игрока уже есть комната"),
            RoomCreateError.TooManyRooms => (409, "too_many_rooms",
                "комнат больше, чем игроков онлайн"),
            RoomCreateError.RoomLimitReached => (503, "room_limit", "на сервере кончились комнаты"),
            _ => (400, "bad_request", "не получилось"),
        };

    private static WebResponse ApiError(int status, string code, string message)
    {
        var json = JsonSerializer.Serialize(new { code, message });
        return WebResponse.Json(json, status);
    }

    private static WebResponse WithAllow(string allow, WebResponse response) => new(
        response.Status,
        response.ContentType,
        response.Body,
        response.RetryAfterSec,
        new List<(string, string)>(response.ExtraHeaders) { ("Allow", allow) });

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        AllowTrailingCommas = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
    };

    /// <summary>Тело <c>POST /api/rooms</c>.</summary>
    /// <param name="Mode">Режим: алиас или id (<c>2v2</c>, <c>Duel</c>).</param>
    /// <param name="Level">Карта; пусто — карта по умолчанию для режима.</param>
    /// <param name="Name">Название комнаты; пусто — придумает реестр.</param>
    /// <param name="Owner">Ник создателя (из лобби).</param>
    private sealed record CreateRoomRequest(string? Mode, string? Level, string? Name, string? Owner);
}

/// <summary>Что роутеру нужно от хоста и конфига.</summary>
public sealed class WebPanelRouterOptions
{
    /// <summary>Режим по умолчанию, если панель не прислала ни одного.</summary>
    public string DefaultModeId { get; init; } = LobbyPropKeys.DefaultGameModeId;

    /// <summary>
    /// Требовать ли ник владельца. Спека — «одна кнопка», ник в лобби есть всегда,
    /// поэтому true; false оставлено для тестов и локальных запусков.
    /// </summary>
    public bool RequireOwnerNick { get; init; } = true;

    /// <summary>
    /// Префикс подпути за прокси (например <c>/vlalchillow</c>). Пусто — панель в корне.
    /// </summary>
    public string BasePath { get; init; } = "";

    /// <summary>
    /// Поднять игровую комнату по записи реестра. Возвращает текст ошибки или <c>null</c>.
    /// </summary>
    public Func<RoomDescriptor, string?>? OnRoomCreated { get; init; }
}

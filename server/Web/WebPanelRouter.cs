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

                // Размер команды: страница показывает выбор только там, где он есть.
                var shape = RoomCapacity.ForMode(mode.GameModeId);
                writer.WriteNumber("perTeam", shape.PerTeam);
                writer.WriteNumber("maxPlayers", shape.Total);
                writer.WriteString("teamLabel", RoomCapacity.Describe(mode.GameModeId, shape.PerTeam));
                writer.WriteStartArray("teamSizes");
                foreach (var size in shape.Choosable)
                    writer.WriteNumberValue(size);
                writer.WriteEndArray();
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
                writer.WriteNumber("ownerId", room.OwnerId);
                writer.WriteString("state", room.State.ToString().ToLowerInvariant());
                writer.WriteNumber("players", room.Players);
                writer.WriteNumber("perTeam", room.PerTeam);
                writer.WriteNumber("maxPlayers",
                    RoomCapacity.TotalFor(room.ModeId, room.PerTeam));
                writer.WriteString("teamLabel",
                    RoomCapacity.Describe(room.ModeId, room.PerTeam));
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

        // Одна кнопка: владелец — тот, кто создаёт. Теперь это ID из лобби, а не ник:
        // ник в лобби не уникален и его можно вписать какой угодно.
        if (parsed.OwnerId <= 0)
        {
            // Прислали ник вместо ID — объясняем прямо, а не «пустое поле»:
            // иначе человек ищет ошибку в форме, где всё заполнено.
            return string.IsNullOrWhiteSpace(parsed.Owner)
                ? ApiError(400, "empty_owner", "укажи свой ID из лобби")
                : ApiError(400, "nick_not_supported",
                    "комната создаётся по ID игрока, а не по нику — возьми ID из чата лобби");
        }

        var ownerNick = string.Empty;
        var online = _options.OnlinePlayers?.Invoke();
        if (online is not null)
        {
            // Ник берём с сервера: человек его не вводит и подделать не может.
            var found = false;
            foreach (var (id, nick) in online)
            {
                if (id != parsed.OwnerId)
                    continue;
                ownerNick = nick;
                found = true;
                break;
            }

            if (!found)
                return ApiError(409, "player_offline",
                    $"игрока с ID {parsed.OwnerId} нет в лобби — зайди в игру и создай комнату оттуда");
        }

        var modeToken = string.IsNullOrWhiteSpace(parsed.Mode)
            ? _options.DefaultModeId
            : parsed.Mode;

        if (!_rooms.TryCreate(
                modeToken, parsed.Level, parsed.Name, ownerNick, out var room, out var error,
                perTeam: parsed.PerTeam, ownerId: parsed.OwnerId)
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
            writer.WriteNumber("ownerId", room.OwnerId);
            writer.WriteString("joinCommand", FormatJoinCommand(room));
            writer.WriteString("state", room.State.ToString().ToLowerInvariant());
            // Потолок и подпись размера — сразу, иначе только что созданная комната
            // рисуется с глобальными 16 и врёт про «есть места» в 1 на 1.
            writer.WriteNumber("perTeam", room.PerTeam);
            writer.WriteNumber("maxPlayers", RoomCapacity.TotalFor(room.ModeId, room.PerTeam));
            writer.WriteString("teamLabel", RoomCapacity.Describe(room.ModeId, room.PerTeam));
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static (int Status, string Code, string Message) MapCreateError(RoomCreateError error) =>
        error switch
        {
            RoomCreateError.UnknownMode => (400, "unknown_mode", "неизвестный режим"),
            RoomCreateError.UnknownLevel => (400, "unknown_level", "такой карты у режима нет"),
            RoomCreateError.EmptyOwner => (400, "empty_owner", "не указан ID владельца"),
            RoomCreateError.DuplicateOwner => (409, "one_room_per_player",
                "у этого игрока уже есть комната"),
            RoomCreateError.TooManyRooms => (409, "too_many_rooms",
                "комнат больше, чем игроков онлайн"),
            RoomCreateError.RoomLimitReached => (503, "room_limit", "на сервере кончились комнаты"),
            RoomCreateError.UnknownTeamSize => (400, "unknown_team_size",
                "этот режим не поддерживает такой размер команды"),
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
    /// <summary>
    /// Тело создания комнаты. <paramref name="PerTeam"/> — размер команды для режимов,
    /// где он выбирается (1 или 2 у союзников и дуэли); <c>0</c> — «как в режиме».
    /// </summary>
    private sealed record CreateRoomRequest(
        string? Mode,
        string? Level,
        string? Name,
        int OwnerId = 0,
        int PerTeam = 0,
        // Оставлено только чтобы отличить «прислали ник» от «не прислали ничего».
        // Значение никак не используется: подпись берётся из списка лобби.
        string? Owner = null);
}

/// <summary>Что роутеру нужно от хоста и конфига.</summary>
public sealed class WebPanelRouterOptions
{
    /// <summary>Режим по умолчанию, если панель не прислала ни одного.</summary>
    public string DefaultModeId { get; init; } = LobbyPropKeys.DefaultGameModeId;

    /// <summary>
    /// Префикс подпути за прокси (например <c>/vlalchillow</c>). Пусто — панель в корне.
    /// </summary>
    public string BasePath { get; init; } = "";

    /// <summary>
    /// Поднять игровую комнату по записи реестра. Возвращает текст ошибки или <c>null</c>.
    /// </summary>
    public Func<RoomDescriptor, string?>? OnRoomCreated { get; init; }

    /// <summary>
    /// Кто сейчас в лобби: (серверный id, ник).
    /// </summary>
    /// <remarks>
    /// Комната создаётся <b>за конкретным присутствующим человеком</b>, а не за
    /// строчкой текста. Без этой проверки можно было вписать любой ник (в том числе
    /// чужой или несуществующий) и получить комнату «за игрока», которого нет.
    /// <c>null</c> — проверка выключена (тесты реестра и режим без хоста).
    /// </remarks>
    public Func<IReadOnlyList<(int Id, string Nick)>>? OnlinePlayers { get; init; }
}

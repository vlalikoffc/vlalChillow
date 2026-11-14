using System.Reflection;
using System.Text;
using System.Text.Json;
using StandChillow.LanServer.Config;
using StandChillow.LanServer.Net;
using StandChillow.LanServer.Net.Lobby;
using StandChillow.LanServer.Net.Match.Kernel;

namespace StandChillow.LanServer.Web;

/// <summary>
/// Панель целиком: реестр комнат, страница, API, фоновое обновление сводки.
/// </summary>
/// <remarks>
/// <para>
/// Один объект на процесс. Создаётся <b>до</b> игровых сокетов: если 7777 занят, страница
/// всё равно открывается и честно показывает состояние, а не «502 Bad Gateway». Поэтому
/// <see cref="Start"/> не зависит от <see cref="GameNetHost"/> и терпит его отсутствие.
/// </para>
/// <para>
/// Игровой хост подключается позже через <see cref="AttachHost"/>: до этого панель
/// показывает ноль игроков, но кнопка создания уже отвечает внятной ошибкой, а не
/// зависает.
/// </para>
/// <para>
/// HTML лежит в сборке как <c>EmbeddedResource</c> — рядом с бинарником нет ни папки
/// <c>wwwroot</c>, ни файлов, которые можно потерять при копировании.
/// </para>
/// </remarks>
public sealed class WebPanelHost : IDisposable
{
    /// <summary>Имя ресурса со страницей.</summary>
    public const string IndexResourceName = "StandChillow.LanServer.Web.wwwroot.index.html";

    private readonly WebConfig _config;
    private readonly ServerConfig _server;
    private readonly RoomRegistry _rooms;
    private readonly StatusSnapshotCache _status = new();
    private readonly SiteAddressResolver _addresses;
    private readonly WebPanelServer _server_;
    private readonly WebPanelRouter _router;
    private readonly CancellationTokenSource _tickCts = new();
    private readonly Func<string> _indexHtml;

    private GameNetHost? _host;
    private Task? _tickLoop;

    public WebPanelHost(ServerConfig server)
    {
        _server = server;
        _config = server.Web;
        _rooms = new RoomRegistry { MaxRooms = Math.Clamp(_config.MaxRooms, 1, 1024) };

        _indexHtml = LoadEmbeddedIndex;
        _addresses = new SiteAddressResolver
        {
            ConfiguredBaseUrl = _config.PublicBaseUrl ?? "",
        };

        var limits = new WebPanelOptions.WebLimits
        {
            RequestsPerSecond = Math.Clamp(_config.Limits.RequestsPerSecond, 1, 1000),
            RefusalsBeforeBlock = Math.Clamp(_config.Limits.RefusalsBeforeBlock, 1, 1000),
            RefusalWindowSec = Math.Clamp(_config.Limits.RefusalWindowSec, 1, 3600),
            BlockDurationSec = Math.Clamp(_config.Limits.BlockDurationSec, 1, 86400),
            MaxHeaderBytes = Math.Clamp(_config.Limits.MaxHeaderBytes, 1024, 64 * 1024),
            MaxBodyBytes = Math.Clamp(_config.Limits.MaxBodyBytes, 256, 64 * 1024),
            RequestTimeoutSec = Math.Clamp(_config.Limits.RequestTimeoutSec, 1, 120),
            MaxConnectionsPerIp = Math.Clamp(_config.Limits.MaxConnectionsPerIp, 1, 64),
        };

        _router = new WebPanelRouter(
            _rooms,
            () => _indexHtml(),
            _status,
            new WebPanelRouterOptions
            {
                OnRoomCreated = CreateGameRoom,
            });

        _server_ = new WebPanelServer(
            new WebPanelOptions
            {
                BindAddress = string.IsNullOrWhiteSpace(_config.BindAddress)
                    ? "127.0.0.1"
                    : _config.BindAddress.Trim(),
                Port = Math.Clamp(_config.Port, 1, 65535),
                Limits = limits,
            },
            _router.Route);
    }

    /// <summary>Реестр комнат — то, что видит и панель, и <c>/join</c>.</summary>
    public RoomRegistry Rooms => _rooms;

    /// <summary>Порт панели; 0, если не поднялась.</summary>
    public int Port => _server_.Port;

    /// <summary>Откуда брать адрес сайта для «создать можно здесь».</summary>
    public SiteAddressResolver SiteAddress => _addresses;

    /// <summary>
    /// Поднять панель. Провал бинда — не катастрофа: возвращаем <c>false</c> и причину,
    /// игра продолжает работать (спека: панель не должна мешать серверу, и наоборот).
    /// </summary>
    public bool TryStart(out string error)
    {
        if (!_config.Enabled)
        {
            error = "web.enabled = false";
            return false;
        }

        if (!_server_.TryStart(out error))
            return false;

        Console.WriteLine(
            $"[web] панель на http://{_server_.BindAddress}:{_server_.Port}/ " +
            $"(комнат до {_rooms.MaxRooms}, лимит {_config.Limits.RequestsPerSecond} запр/с с IP)");

        _tickLoop = Task.Run(() => TickLoopAsync(_tickCts.Token));
        return true;
    }

    /// <summary>
    /// Подключить игровой хост: с этого момента панель видит игроков, а создание комнаты
    /// реально открывает её на 7777.
    /// </summary>
    public void AttachHost(GameNetHost host)
    {
        _host = host;
        host.SiteAddress = _addresses;
        host.Match.Rooms = _rooms;

        // «Комнат не больше, чем игроков онлайн»: квота реестра спрашивает у хоста,
        // сколько людей сейчас в лобби и в катке. Пока хост не подключён, считаем 0.
        _rooms.OnlinePlayerCount = () => host.JoinedCount + host.Match.HumanActorCount;
        host.Match.PendingRooms.Warning += msg => Console.WriteLine(msg);

        Console.WriteLine(
            $"[web] игровой хост подключён: онлайн {_rooms.OnlinePlayerCount()}, " +
            $"квота комнат по игрокам активна");
    }

    /// <summary>Остановить панель (сокет + фоновая задача).</summary>
    public void Stop()
    {
        try { _tickCts.Cancel(); }
        catch { /* уже отменён */ }
        try { _tickLoop?.Wait(TimeSpan.FromSeconds(2)); }
        catch { /* отмена — норма */ }
        _server_.Stop();
    }

    public void Dispose()
    {
        Stop();
        _server_.Dispose();
        _tickCts.Dispose();
    }

    // ── фоновая сводка ───────────────────────────────────────────────────────

    private async Task TickLoopAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                // Раз в секунду: заодно закрывает комнаты, из которых все ушли.
                _status.Refresh(BuildStatusJson);
                SweepExpiredRooms();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[web] фоновая задача: {ex.Message}");
            }

            try { await Task.Delay(StatusSnapshotCache.RefreshInterval, token); }
            catch (OperationCanceledException) { return; }
        }
    }

    /// <summary>
    /// Удалить комнаты, которые больше не нужны: пустые дольше TTL и завершённые.
    /// </summary>
    /// <remarks>
    /// Удаление обязательно снимает и бронь ника: иначе игрок, чья комната закрылась,
    /// не смог бы создать новую, а <c>/join</c> вёл бы в мёртвый ключ.
    /// </remarks>
    private void SweepExpiredRooms()
    {
        var emptyTtl = TimeSpan.FromSeconds(Math.Clamp(_config.EmptyRoomTtlSec, 60, 86400));
        var expired = _rooms.FindExpired(DateTime.UtcNow, emptyTtl, emptyTtl);
        if (expired.Count == 0)
            return;

        foreach (var number in expired)
        {
            if (!_rooms.TryRemove(number, out var removed))
                continue;

            _host?.Match.PendingRooms.Release(removed.PasswordKey);
            Console.WriteLine(
                $"[web] комната #{removed.Number} «{removed.Name}» закрыта " +
                $"({(removed.Players > 0 ? "катка закончилась" : "никто не пришёл")})");
        }
    }

    // ── JSON для панели ──────────────────────────────────────────────────────

    private string BuildStatusJson()
    {
        var host = _host;
        var lobby = host?.JoinedCount ?? 0;
        var match = host?.Match.HumanActorCount ?? 0;
        var rooms = _rooms.Snapshot();

        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, JsonText.WriterOptions))
        {
            writer.WriteStartObject();
            writer.WriteNumber("onlineLobby", lobby);
            writer.WriteNumber("onlineMatch", match);
            writer.WriteNumber("onlineTotal", lobby + match);
            writer.WriteNumber("roomsCount", rooms.Count);
            writer.WriteNumber("roomsMax", _rooms.MaxRooms);
            writer.WriteNumber("nextNumber", _rooms.NextNumber);
            writer.WriteBoolean("gameListening", host?.Match.IsListening ?? false);
            writer.WriteBoolean("matchRunning", host?.MatchStarted ?? false);
            writer.WriteString("mode", host?.Session.GameModeId ?? "");
            writer.WriteString("levels", string.Join(", ", host?.Session.SelectedLevels
                ?? new List<string>()));
            writer.WriteString("serverUtc", DateTime.UtcNow.ToString("O"));
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    // ── создание игровой комнаты ─────────────────────────────────────────────

    /// <summary>
    /// Превратить запись реестра в настоящую комнату матч-хоста и забронировать её за
    /// создателем. Возвращает текст ошибки или <c>null</c> при успехе.
    /// </summary>
    /// <remarks>
    /// Комната создаётся уже «armed»: из панели её открывают, чтобы играть, ручного
    /// <c>/set start</c> в этом сценарии нет. Игрок попадает в неё, зайдя в игру —
    /// бронь по нику разворачивает <c>JoinRoom(nick, 'Dedik')</c> в нужную комнату.
    /// </remarks>
    private string? CreateGameRoom(RoomDescriptor room)
    {
        var host = _host;
        if (host is null)
            return "игровой сервер ещё не поднялся — попробуй через пару секунд";

        var created = host.Match.TryCreatePanelRoom(
            room.PasswordKey,
            room.ModeId,
            room.Level,
            room.Number,
            BuildRoomLabel(room));

        if (created is null)
            return "комната с таким номером уже существует — обнови страницу";

        if (!host.Match.PendingRooms.Reserve(room.PasswordKey, room.OwnerNick))
            return "слишком много ожидающих комнат — попробуй ещё раз";

        host.Match.ArmRoomByPasswordKey(
            room.PasswordKey, $"создана с сайта, #{room.Number}");

        Console.WriteLine(
            $"[web] создана комната #{room.Number} «{BuildRoomLabel(room)}» " +
            $"({room.ModeId} / {room.Level}) владелец '{room.OwnerNick}' — " +
            $"ждать: /join {GameModeCatalogAlias(room.ModeId)} #{room.Number}");
        return null;
    }

    /// <summary>Подпись комнаты для лога и страницы: имя или «Комната #N».</summary>
    public static string BuildRoomLabel(RoomDescriptor room) =>
        string.IsNullOrWhiteSpace(room.Name) ? $"Комната #{room.Number}" : room.Name;

    private static string GameModeCatalogAlias(string modeId) =>
        GameModeCatalog.TryGetMode(modeId, out var mode)
            ? GameModeCatalog.PrimaryAlias(mode)
            : modeId;

    // ── страница ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Прочитать вшитый HTML. Файла рядом с бинарником нет — только ресурс сборки.
    /// </summary>
    private static string LoadEmbeddedIndex()
    {
        var assembly = Assembly.GetExecutingAssembly();
        using var stream = assembly.GetManifestResourceStream(IndexResourceName);
        if (stream is null)
        {
            var known = string.Join(", ",
                assembly.GetManifestResourceNames()
                    .Where(n => n.Contains("index.html", StringComparison.OrdinalIgnoreCase)));
            Console.WriteLine(
                $"[web] FATAL: ресурс '{IndexResourceName}' не найден " +
                $"(html-ресурсы: {known}) — панель отдаст заглушку");
            return FallbackPage;
        }

        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }

    /// <summary>
    /// Заглушка, если ресурс не вшит. Не пустая страница: человек должен понять, что
    /// сервер жив, а сломан именно HTML.
    /// </summary>
    private const string FallbackPage =
        "<!doctype html><html lang=\"ru\"><meta charset=\"utf-8\">" +
        "<title>влалChillow</title>" +
        "<body style=\"font:16px system-ui;padding:24px\">" +
        "<h1>влалChillow</h1>" +
        "<p>Сервер работает, но страница панели не вшита в сборку " +
        "(<code>EmbeddedResource wwwroot/index.html</code>).</p></body></html>";
}

using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace StandChillow.LanServer.Config;

/// <summary>Как разрешено начинать матч.</summary>
public enum MatchStartMode
{
    /// <summary>Только по команде <c>/start</c> / <c>/set start</c> (права по <c>admin</c>).</summary>
    Manual,

    /// <summary>Сам, когда в каждой команде набралось <c>auto.minPlayersPerTeam</c> игроков.</summary>
    Auto,

    /// <summary>Когда все игроки, находящиеся в катке, написали <c>/ready</c>.</summary>
    Ready,
}

/// <summary>Кто имеет право крутить серверные команды.</summary>
/// <remarks>
/// LAN по умолчанию: играют свои, поэтому никто не обязан быть админом. Пустой
/// <see cref="AdminOnlyCommands"/> + <see cref="AnyoneCanStart"/> = true значит «команды доступны
/// всем». Кто хочет ограничить — вписывает ники в <see cref="Nicknames"/> и команды в
/// <see cref="AdminOnlyCommands"/>.
/// </remarks>
public sealed class AdminConfig
{
    /// <summary>Ники администраторов (регистр не важен, пробелы по краям игнорируются).</summary>
    public List<string> Nicknames { get; set; } = new();

    /// <summary>
    /// Команды только для админов. По умолчанию <c>set</c> и <c>end</c>: они меняют важные
    /// параметры хоста (раунды, победу, состав команды) и обрывают идущую катку.
    /// </summary>
    /// <remarks>
    /// Запуск матча <b>специально</b> сюда не входит: <c>/mode</c>, <c>/map</c>, <c>/start</c>,
    /// <c>/play</c> остаются доступны всем — катку может начать кто угодно. Это же покрывает
    /// <see cref="AnyoneCanStart"/> для <c>/start</c> и <c>/set start</c>.
    /// </remarks>
    public List<string> AdminOnlyCommands { get; set; } = DefaultAdminOnlyCommands();

    /// <summary>Разрешить /start и /set start любому игроку (по умолчанию да).</summary>
    public bool AnyoneCanStart { get; set; } = true;

    /// <summary>Баны: срок, файл, кто может банить.</summary>
    public BanConfig Ban { get; set; } = new();

    /// <summary>Команды, закрытые админом «из коробки».</summary>
    public static List<string> DefaultAdminOnlyCommands() => new() { "set", "end" };
}

/// <summary>
/// Баны. По умолчанию <c>/ban</c>, <c>/unban</c> и <c>/bans</c> — только для админов
/// (независимо от <see cref="AdminConfig.AdminOnlyCommands"/>), админа забанить нельзя.
/// </summary>
/// <remarks>
/// Срок ограничен с двух сторон: минимум — чтобы «бан на секунду» не был способом
/// потроллить, максимум — потому что IP на LAN выдаёт DHCP и вечный бан адреса
/// рано или поздно достанется ни в чём не виноватому телефону.
/// </remarks>
public sealed class BanConfig
{
    /// <summary>Выключатель на весь модуль (команды отвечают «баны выключены»).</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>true — /ban, /unban, /bans только админам (по умолчанию).</summary>
    public bool AdminOnly { get; set; } = true;

    /// <summary>Имя файла банов рядом с config.json (или полный путь).</summary>
    public string File { get; set; } = "banlist.json";

    /// <summary>Минимальный срок бана, минут.</summary>
    public int MinMinutes { get; set; } = 10;

    /// <summary>Максимальный срок бана, дней (14 — как в ТЗ).</summary>
    public int MaxDays { get; set; } = 14;

    /// <summary>
    /// Учить новые ники/IP на попытке входа: сменил ник, но IP тот же —
    /// ник тоже попадёт в бан; сменил IP под тем же ником — попадёт IP.
    /// </summary>
    public bool BindAliases { get; set; } = true;

    /// <summary>Сообщать всем в лобби, что кого-то забанили.</summary>
    public bool NotifyEveryone { get; set; } = true;

    /// <summary>Как часто обновлять «Разбан через: …» в теневом лобби, секунд.</summary>
    public int ShadowRefreshSec { get; set; } = 60;

    /// <summary>Как часто проверять, у кого вышел срок, секунд.</summary>
    public int SweepSec { get; set; } = 5;

    /// <summary>
    /// Отдавать в матч серверный id вместо клиентского <c>uid</c> («Offline»): тогда в игре
    /// в списке игроков видно число, которое принимает <c>/banid</c>. Выключить — клиент
    /// снова покажет свою заглушку.
    /// </summary>
    public bool ExposeIds { get; set; } = true;
}

/// <summary>Настройки автостарта (<c>matchStart.mode = "auto"</c>).</summary>
public sealed class AutoStartConfig
{
    /// <summary>Минимум игроков в каждой команде для автозапуска.</summary>
    public int MinPlayersPerTeam { get; set; } = 1;
}

/// <summary>Настройки старта по готовности (<c>matchStart.mode = "ready"</c>).</summary>
public sealed class ReadyStartConfig
{
    /// <summary>Писать в чат «ник готов (N/M)».</summary>
    public bool AnnounceInChat { get; set; } = true;

    /// <summary>Снимать голос игрока, вышедшего из катки.</summary>
    public bool ResetOnLeave { get; set; } = true;

    /// <summary>Разрешить /unready.</summary>
    public bool AllowUnready { get; set; } = true;
}

/// <summary>Как начинается матч.</summary>
public sealed class MatchStartConfig
{
    /// <summary>manual | auto | ready. По умолчанию auto — матч стартует сам.</summary>
    public string Mode { get; set; } = "auto";

    public AutoStartConfig Auto { get; set; } = new();

    public ReadyStartConfig Ready { get; set; } = new();
}

/// <summary>
/// Блок «запуск»: то, что обычно пишут в командной строке.
/// Читает <b>только выделенный сервер</b>. Режим <c>--client</c> (проба) этот
/// блок не читает — у пробы свои аргументы и свои настройки.
/// </summary>
public sealed class LaunchConfig
{
    /// <summary>
    /// Доп. аргументы, как будто дописаны после exe:
    /// <c>--verbose</c>, <c>--no-dashboard</c>, <c>--debug-chat</c>.
    /// <c>--client</c> запрещён: конфиг не переключает сервер в пробу.
    /// </summary>
    public List<string> Args { get; set; } = new();

    /// <summary>Дашборд: true/false (null — авто по терминалу).</summary>
    public bool? Dashboard { get; set; }

    /// <summary>Название лобби (null — «влал хостит рялна»).</summary>
    public string? Title { get; set; }

    /// <summary>Подробный лог пакетов.</summary>
    public bool Verbose { get; set; }
}

/// <summary>Имена, порты и лимиты лобби.</summary>
public sealed class ServerSectionConfig
{
    /// <summary>Приветствие в чат при входе ("" — не слать).</summary>
    public string? WelcomeMessage { get; set; }

    /// <summary>Максимум игроков в лобби.</summary>
    public int MaxMembers { get; set; } = 16;

    /// <summary>Порт лобби (UDP).</summary>
    public int LobbyPort { get; set; } = 7778;
}

/// <summary>Сеть: где слушать и пускать ли VPN/виртуальные адаптеры.</summary>
public sealed class NetworkConfig
{
    /// <summary>
    /// На каком адресе открывать сервер. <c>0.0.0.0</c> — все интерфейсы сразу
    /// (Wi‑Fi, Ethernet, VPN, Hamachi, Radmin). Пусто — авто-выбор LAN-адреса.
    /// </summary>
    public string BindAddress { get; set; } = "0.0.0.0";

    /// <summary>
    /// true — не трогать VPN/виртуальные адаптеры (только Wi‑Fi/Ethernet).
    /// false — слушать везде: VPN, Hamachi, Radmin, ZeroTier и т.п.
    /// </summary>
    public bool IgnoreVpn { get; set; } = true;

    /// <summary>
    /// Какой IP писать телефонам в op9 (адрес матча). Пусто — авто по LAN.
    /// Нужен, когда играете через Hamachi/Radmin и авто берёт не тот адрес.
    /// </summary>
    public string? AdvertiseIp { get; set; }
}

/// <summary>Длительности фаз матча в секундах.</summary>
public sealed class TimingsConfig
{
    /// <summary>WarmUp C2=21.</summary>
    public int Warmup { get; set; } = 3;

    /// <summary>PreStart C2=22.</summary>
    public int PreStart { get; set; } = 3;

    /// <summary>Prep C2=31 (закупка).</summary>
    public int Prep { get; set; } = 10;

    /// <summary>Длина раунда, сек.</summary>
    public int RoundTime { get; set; } = 109;

    /// <summary>Пауза после RoundEnd.</summary>
    public int RoundEndPause { get; set; } = 6;

    /// <summary>Фитиль бомбы.</summary>
    public int BombFuse { get; set; } = 40;

    /// <summary>MR-N: раундов в серии.</summary>
    public int TotalRounds { get; set; } = 8;

    /// <summary>Победа до N раундов (0 — дефолт режима).</summary>
    public int WinsNeeded { get; set; }

    /// <summary>Стартовые деньги.</summary>
    public int RoundStartMoney { get; set; } = 800;
}

/// <summary>Горячие ручки хоста: темп цикла, бэкап, пул пакетов.</summary>
public sealed class TuningConfig
{
    /// <summary>Период игрового цикла хоста, мс.</summary>
    public int TickMs { get; set; } = 33;

    /// <summary>Частота рассылки поз, мс.</summary>
    public int StateIntervalMs { get; set; } = 50;

    /// <summary>Порог очереди надёжных пакетов (дропаем только косметику).</summary>
    public int ReliableQueueSoftLimit { get; set; } = 256;

    /// <summary>Пул пакетов LiteNetLib.</summary>
    public int PacketPoolSize { get; set; } = 4096;

    /// <summary>Таймаут отключения пира, мс.</summary>
    public int DisconnectTimeoutMs { get; set; } = 10000;

    /// <summary>UpdateTime LiteNetLib, мс.</summary>
    public int NetUpdateTimeMs { get; set; } = 15;

    /// <summary>PingInterval LiteNetLib, мс.</summary>
    public int PingIntervalMs { get; set; } = 1000;

    /// <summary>Интервал строки [health], сек (0 — выключить).</summary>
    public int HealthIntervalSec { get; set; } = 10;
}

/// <summary>
/// Отладка. Действует <b>только на выделенном сервере</b>:
/// режим <c>--client</c> (проба) этот блок не читает и не может его изменить.
/// </summary>
public sealed class DebugConfig
{
    /// <summary>Подробный per-packet лог.</summary>
    public bool Verbose { get; set; }

    /// <summary>Писать capture-файлы.</summary>
    public bool Captures { get; set; }

    /// <summary>Каталог capture-файлов (null — <c>captures</c> рядом с exe).</summary>
    public string? CapturesDir { get; set; }

    /// <summary>Максимум capture-файлов за запуск (0 — без лимита).</summary>
    public int CapturesMaxFiles { get; set; } = 4000;

    /// <summary>Максимум мегабайт capture-файлов (0 — без лимита).</summary>
    public int CapturesMaxBytesMb { get; set; } = 64;

    /// <summary>Отладочные сообщения хоста в чат.</summary>
    public bool MatchChat { get; set; }

    /// <summary>Строка [health].</summary>
    public bool Health { get; set; } = true;

    /// <summary>Логировать каждый принятый пакет (очень шумно).</summary>
    public bool LogPackets { get; set; }

    /// <summary>Логировать каждый discovery-probe.</summary>
    public bool LogDiscovery { get; set; }
}

/// <summary>
/// Корневой конфиг сервера: <c>config.json</c> рядом с бинарником.
/// Нет файла — создаётся шаблон, работа продолжается на дефолтах.
/// </summary>
public sealed class ServerConfig
{
    public const string FileName = "config.json";

    /// <summary>Запуск: аргументы, дашборд, название.</summary>
    public LaunchConfig Launch { get; set; } = new();

    /// <summary>Лобби: приветствие, лимит игроков.</summary>
    public ServerSectionConfig Server { get; set; } = new();

    /// <summary>Сеть: адрес прослушивания, VPN-адаптеры, адрес в op9.</summary>
    public NetworkConfig Network { get; set; } = new();

    public AdminConfig Admin { get; set; } = new();

    public MatchStartConfig MatchStart { get; set; } = new();

    public TimingsConfig Timings { get; set; } = new();

    public TuningConfig Tuning { get; set; } = new();

    public DebugConfig Debug { get; set; } = new();

    /// <summary>
    /// Замечание к конфигу: какое значение недопустимо и что применилось вместо него.
    /// </summary>
    /// <remarks>
    /// Раньше «битое» значение молча заменялось стандартным, и понять, почему сервер
    /// играет не так, как написано в файле, было невозможно. Теперь каждое такое
    /// исправление попадает в лог одной строкой.
    /// </remarks>
    public readonly record struct ConfigIssue(
        string Key,
        string Detail,
        string Applied,
        bool RequiresRestart = false);

    private readonly List<ConfigIssue> _issues = new();

    /// <summary>Что пришлось исправить в этом конфиге (пусто — всё как написано).</summary>
    /// <remarks>
    /// <see cref="JsonIgnoreAttribute"/> обязателен: без него <c>System.Text.Json</c> при
    /// десериализации попытается заполнить это свойство без сеттера и бросит исключение —
    /// ровно так «конфиг никогда не читается» и выглядело.
    /// </remarks>
    [JsonIgnore]
    public IReadOnlyList<ConfigIssue> Issues => _issues;

    /// <summary>Откуда конфиг загружен в последний раз.</summary>
    public static string LastLoadedPath { get; private set; } = DefaultPath;

    /// <summary>Последний загруженный конфиг.</summary>
    public static ServerConfig Current { get; private set; } = new();

    public static string DefaultPath => Path.Combine(AppContext.BaseDirectory, FileName);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        // В шаблоне есть // — их надо пропускать, иначе первый запуск = дефолты.
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
    };

    /// <summary>Читает конфиг; нет файла — пишет шаблон. Никогда не бросает.</summary>
    public static ServerConfig Load(string? path = null)
    {
        var target = string.IsNullOrWhiteSpace(path) ? DefaultPath : path;
        LastLoadedPath = target;

        if (!File.Exists(target))
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(target) ?? ".");
                File.WriteAllText(target, Template);
                Console.WriteLine($"[config] создан шаблон: {target}");
                Console.WriteLine(
                    "[config] команды открыты всем; ограничить — admin.nicknames + " +
                    "admin.adminOnlyCommands");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[config] не смог создать '{target}': {ex.Message}");
            }

            // Только что созданный файл = новый шаблон; Normalize нужен, чтобы
            // действующие значения совпадали с тем, что написано на диске.
            Current = new ServerConfig();
            Current.Normalize();
            return Current;
        }

        if (TryLoad(target, out var cfg, out var error))
        {
            Current = cfg;
            Console.WriteLine($"[config] загружен: {target}");
            return cfg;
        }

        Console.WriteLine($"[config] ошибка чтения '{target}': {error}");
        Console.WriteLine("[config] работаю на дефолтах — файл НЕ изменён");
        Current = new ServerConfig();
        Current.Normalize();
        return Current;
    }

    /// <summary>
    /// Пытается прочитать существующий файл. <c>false</c> — файла нет, он недоступен или
    /// это не валидный JSON; в этом случае <paramref name="config"/> не используется.
    /// </summary>
    /// <remarks>
    /// Отдельно от <see cref="Load"/> нужно живому перечитыванию: если файл сохранился
    /// «полузаписанным», сервер обязан остаться на прежних настройках, а не заменить
    /// весь конфиг дефолтами.
    /// </remarks>
    public static bool TryLoad(string path, out ServerConfig config, out string error)
    {
        config = new ServerConfig();
        error = string.Empty;

        try
        {
            if (!File.Exists(path))
            {
                error = "файл не найден";
                return false;
            }

            var parsed = JsonSerializer.Deserialize<ServerConfig>(
                File.ReadAllText(path), JsonOptions);
            if (parsed is null)
            {
                error = "файл пустой";
                return false;
            }

            parsed.Normalize();
            parsed.LogIssues();
            config = parsed;
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    /// <summary>
    /// Проверяет значения и заменяет недопустимые стандартными.
    /// Каждая замена попадает в <see cref="Issues"/> — молча ничего не исправляется.
    /// </summary>
    public void Normalize()
    {
        _issues.Clear();

        Launch ??= new LaunchConfig();
        Launch.Args ??= new List<string>();
        Server ??= new ServerSectionConfig();
        Network ??= new NetworkConfig();
        Admin ??= new AdminConfig();
        Admin.Nicknames ??= new List<string>();
        Admin.AdminOnlyCommands ??= new List<string>();
        Admin.Ban ??= new BanConfig();
        MatchStart ??= new MatchStartConfig();
        MatchStart.Auto ??= new AutoStartConfig();
        MatchStart.Ready ??= new ReadyStartConfig();
        Timings ??= new TimingsConfig();
        Tuning ??= new TuningConfig();
        Debug ??= new DebugConfig();

        // Прова в пробу конфигом запрещена — иначе весь блок debug «утечёт» в пробу.
        // Регистр не важен: --client / -c / ConnectAsClient / --connectasclient.
        Launch.Args.RemoveAll(IsProbeFlag);

        MatchStart.Auto.MinPlayersPerTeam = Check(
            MatchStart.Auto.MinPlayersPerTeam, 1, 16, 1, "matchStart.auto.minPlayersPerTeam");

        MigrateLegacyAdminGate();

        // Баны: 10 минут — 14 дней по умолчанию; границы правятся в конфиге.
        Admin.Ban.MinMinutes = Check(
            Admin.Ban.MinMinutes, 1, 1440, 10, "admin.ban.minMinutes");
        Admin.Ban.MaxDays = Check(
            Admin.Ban.MaxDays, 1, 365, 14, "admin.ban.maxDays");
        if (Admin.Ban.MaxDays * 1440 < Admin.Ban.MinMinutes)
        {
            var fixedDays = (Admin.Ban.MinMinutes + 1439) / 1440;
            Report("admin.ban.maxDays", Admin.Ban.MaxDays.ToString(), fixedDays.ToString(),
                $"меньше минимального срока ({Admin.Ban.MinMinutes} мин) — поднял до него");
            Admin.Ban.MaxDays = fixedDays;
        }
        Admin.Ban.ShadowRefreshSec = Check(
            Admin.Ban.ShadowRefreshSec, 10, 600, 60, "admin.ban.shadowRefreshSec");
        Admin.Ban.SweepSec = Check(
            Admin.Ban.SweepSec, 1, 300, 5, "admin.ban.sweepSec");
        Admin.Ban.File = string.IsNullOrWhiteSpace(Admin.Ban.File)
            ? "banlist.json"
            : Admin.Ban.File.Trim();

        // Пустая строка — «как в дефолте» (auto), а не тихий откат в manual:
        // иначе правка конфига руками молча выключала автостарт.
        if (!TryParseMode(MatchStart.Mode, out var startMode))
        {
            Report("matchStart.mode", MatchStart.Mode, "auto",
                "не manual/auto/ready — старт по игрокам");
            MatchStart.Mode = "auto";
        }
        else
        {
            MatchStart.Mode = startMode switch
            {
                MatchStartMode.Auto => "auto",
                MatchStartMode.Ready => "ready",
                _ => "manual",
            };
        }

        // Лобби: порт меняется только перезапуском — слушатель создаётся один раз.
        Server.MaxMembers = Check(Server.MaxMembers, 1, 255, 16, "server.maxMembers");
        Server.LobbyPort = CheckRestart(Server.LobbyPort, 1, 65535, 7778, "server.lobbyPort");

        // Пустой bindAddress — «как раньше»: 0.0.0.0 (все интерфейсы).
        var bind = (Network.BindAddress ?? string.Empty).Trim();
        if (bind.Length == 0)
            Network.BindAddress = "0.0.0.0";
        else if (!IPAddress.TryParse(bind, out _))
        {
            Report("network.bindAddress", Network.BindAddress, "0.0.0.0",
                "не IP-адрес — слушаю все интерфейсы", requiresRestart: true);
            Network.BindAddress = "0.0.0.0";
        }
        else
            Network.BindAddress = bind;

        if (!string.IsNullOrWhiteSpace(Network.AdvertiseIp))
        {
            var advertise = Network.AdvertiseIp.Trim();
            if (IPAddress.TryParse(advertise, out _))
            {
                Network.AdvertiseIp = advertise;
            }
            else
            {
                Report("network.advertiseIp", Network.AdvertiseIp, "(авто)",
                    "не IP-адрес — адрес матча выбирается автоматически", requiresRestart: true);
                Network.AdvertiseIp = null;
            }
        }

        Timings.Warmup = Check(Timings.Warmup, 0, 120, 3, "timings.warmup");
        Timings.PreStart = Check(Timings.PreStart, 1, 120, 3, "timings.preStart");
        Timings.Prep = Check(Timings.Prep, 1, 120, 10, "timings.prep");
        Timings.RoundTime = Check(Timings.RoundTime, 10, 600, 109, "timings.roundTime");
        Timings.RoundEndPause = Check(Timings.RoundEndPause, 1, 30, 6, "timings.roundEndPause");
        Timings.BombFuse = Check(Timings.BombFuse, 5, 120, 40, "timings.bombFuse");
        Timings.TotalRounds = Check(Timings.TotalRounds, 1, 64, 8, "timings.totalRounds");
        Timings.WinsNeeded = Check(Timings.WinsNeeded, 0, 64, 0, "timings.winsNeeded");
        Timings.RoundStartMoney = Check(Timings.RoundStartMoney, 0, 16000, 800, "timings.roundStartMoney");

        Tuning.TickMs = Check(Tuning.TickMs, 5, 200, 33, "tuning.tickMs");
        Tuning.StateIntervalMs = Check(Tuning.StateIntervalMs, 10, 500, 50, "tuning.stateIntervalMs");
        Tuning.ReliableQueueSoftLimit = Check(
            Tuning.ReliableQueueSoftLimit, 16, 4096, 256, "tuning.reliableQueueSoftLimit");
        // Пул пакетов и таймауты зашиты в LiteNetLib при создании слушателя — только рестарт.
        Tuning.PacketPoolSize = CheckRestart(
            Tuning.PacketPoolSize, 64, 65536, 4096, "tuning.packetPoolSize");
        Tuning.DisconnectTimeoutMs = CheckRestart(
            Tuning.DisconnectTimeoutMs, 2000, 60000, 10000, "tuning.disconnectTimeoutMs");
        Tuning.NetUpdateTimeMs = CheckRestart(
            Tuning.NetUpdateTimeMs, 1, 100, 15, "tuning.netUpdateTimeMs");
        Tuning.PingIntervalMs = CheckRestart(
            Tuning.PingIntervalMs, 100, 10000, 1000, "tuning.pingIntervalMs");
        Tuning.HealthIntervalSec = Check(
            Tuning.HealthIntervalSec, 0, 3600, 10, "tuning.healthIntervalSec");

        Debug.CapturesMaxFiles = Check(
            Debug.CapturesMaxFiles, 0, 1_000_000, 4000, "debug.capturesMaxFiles");
        Debug.CapturesMaxBytesMb = Check(
            Debug.CapturesMaxBytesMb, 0, 65536, 64, "debug.capturesMaxBytesMb");
    }

    /// <summary>Разбирает JSON конфига тем же парсером, что и <see cref="Load"/>.</summary>
    /// <remarks>Нужен тестам: проверяют и шаблон, и «битые» файлы без обращения к диску.</remarks>
    public static ServerConfig ParseForTests(string json) =>
        JsonSerializer.Deserialize<ServerConfig>(json, JsonOptions) ?? new ServerConfig();

    /// <summary>
    /// Печатает замечания к конфигу. Ничего не «проглатывается»: оператор видит и
    /// ключ, и что там было написано, и что применилось вместо этого.
    /// </summary>
    public void LogIssues(Action<string>? log = null)
    {
        log ??= Console.WriteLine;

        if (_issues.Count == 0)
        {
            log("[config] все значения корректны");
            return;
        }

        log($"[config] некорректных значений: {_issues.Count} — применены стандартные");
        foreach (var issue in _issues)
        {
            var restart = issue.RequiresRestart ? " (нужен перезапуск)" : "";
            log($"[config]   {issue.Key}: {issue.Detail} → {issue.Applied}{restart}");
        }
    }

    /// <summary>
    /// Проверяет число: вне диапазона → стандартное значение + запись в <see cref="Issues"/>.
    /// </summary>
    private int Check(int value, int min, int max, int fallback, string key)
    {
        if (value >= min && value <= max)
            return value;

        _issues.Add(new ConfigIssue(
            key,
            $"{value} вне диапазона {min}..{max}",
            fallback.ToString()));
        return fallback;
    }

    /// <summary>Как <see cref="Check"/>, но значение применяется только после перезапуска.</summary>
    private int CheckRestart(int value, int min, int max, int fallback, string key)
    {
        if (value >= min && value <= max)
            return value;

        _issues.Add(new ConfigIssue(
            key,
            $"{value} вне диапазона {min}..{max}",
            fallback.ToString(),
            RequiresRestart: true));
        return fallback;
    }

    /// <summary>Записывает замечание по конфигу (значение уже заменено вызывающим).</summary>
    private void Report(string key, string? raw, string applied, string detail,
        bool requiresRestart = false)
    {
        var shown = string.IsNullOrWhiteSpace(raw) ? "(пусто)" : $"'{raw}'";
        _issues.Add(new ConfigIssue(key, $"{shown} {detail}", applied, requiresRestart));
    }

    /// <summary>
    /// Старые версии писали в <c>config.json</c> «админ-гейт по умолчанию»: список админов
    /// пустой, а <c>adminOnlyCommands</c> — полный набор команд с <c>anyoneCanStart: false</c>.
    /// Такая комбинация блокирует команды <b>у всех сразу</b> (админов нет — значит нет и того,
    /// кому они разрешены), поэтому это не намеренная настройка, а дефект прошлого дефолта.
    /// </summary>
    /// <remarks>
    /// Важно: <see cref="Load"/> не перезаписывает существующий файл, поэтому без этой
    /// миграции у того, кто уже запускал сервер, старые значения остались бы навсегда —
    /// и «поменять дефолты» ничего бы ему не дало. Пользовательские настройки не затираются:
    /// миграция срабатывает только на точном старом наборе и только когда админов нет.
    /// </remarks>
    private void MigrateLegacyAdminGate()
    {
        if (Admin.Nicknames.Any(n => !string.IsNullOrWhiteSpace(n)))
            return;

        if (Admin.AnyoneCanStart)
            return;

        if (!IsLegacyDefaultAdminOnlySet())
            return;

        // Новый дефолт: закрыты только set и end — они меняют параметры хоста и
        // обрывают катку. Запуск матча (/mode /map /start /play) остаётся всем.
        Admin.AdminOnlyCommands.Clear();
        Admin.AdminOnlyCommands.AddRange(AdminConfig.DefaultAdminOnlyCommands());
        Admin.AnyoneCanStart = true;
        Console.WriteLine(
            "[config] миграция: старый дефолт «все команды только для админов» при пустом " +
            "admin.nicknames блокировал команды всем — теперь админские только " +
            $"{string.Join(", ", Admin.AdminOnlyCommands)}, запуск катки доступен всем");
    }

    /// <summary>Точный набор команд из прежнего шаблона (<c>mode,map,set,play,end,start</c>).</summary>
    private bool IsLegacyDefaultAdminOnlySet()
    {
        string[] legacy = ["mode", "map", "set", "play", "end", "start"];
        if (Admin.AdminOnlyCommands.Count != legacy.Length)
            return false;

        var actual = new HashSet<string>(
            Admin.AdminOnlyCommands
                .Where(c => !string.IsNullOrWhiteSpace(c))
                .Select(c => c.Trim().ToLowerInvariant()),
            StringComparer.Ordinal);

        return legacy.All(actual.Contains);
    }

    /// <summary>Флаг, который включил бы режим пробы (запрещён в <c>launch.args</c>).</summary>
    public static bool IsProbeFlag(string? arg)
    {
        var a = arg?.Trim();
        return a is not null
            && (a.Equals("--client", StringComparison.OrdinalIgnoreCase)
                || a.Equals("-c", StringComparison.OrdinalIgnoreCase)
                || a.Equals("connectasclient", StringComparison.OrdinalIgnoreCase)
                || a.Equals("--connectasclient", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Разобранный режим старта. Непонятное значение — <c>auto</c> (тот же фолбэк, что и в
    /// <see cref="Normalize"/>): тихий уход в <c>manual</c> означал «матч сам не начнётся»,
    /// и это выглядело как будто сервер игнорирует игроков.
    /// </summary>
    public MatchStartMode StartMode => TryParseMode(MatchStart.Mode, out var mode)
        ? mode
        : MatchStartMode.Auto;

    public static bool TryParseMode(string? raw, out MatchStartMode mode)
    {
        mode = MatchStartMode.Manual;
        if (string.IsNullOrWhiteSpace(raw))
            return false;

        switch (raw.Trim().ToLowerInvariant())
        {
            case "manual":
            case "manually":
            case "ручной":
                mode = MatchStartMode.Manual;
                return true;
            case "auto":
            case "automatic":
            case "авто":
                mode = MatchStartMode.Auto;
                return true;
            case "ready":
            case "rdy":
            case "готовность":
                mode = MatchStartMode.Ready;
                return true;
            default:
                return false;
        }
    }

    /// <summary>
    /// Админ ли ник: без учёта регистра, пробелов по краям, невидимых символов и
    /// формы Unicode (NFC/NFD). Кириллический ник, набранный «одинаково», может
    /// отличаться байтами — из-за этого «влал» не находился в списке.
    /// </summary>
    public bool IsAdmin(string? nick)
    {
        var normalized = NormalizeNick(nick);
        if (normalized.Length == 0)
            return false;

        foreach (var candidate in Admin.Nicknames)
        {
            if (string.Equals(NormalizeNick(candidate), normalized, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    /// <summary>
    /// Приводит ник к сравнимому виду: обрезка, NFC-нормализация Unicode, удаление
    /// невидимых символов (zero-width, BOM, мягкие переносы) и регистронезависимое
    /// сравнение на стороне вызывающего.
    /// </summary>
    public static string NormalizeNick(string? nick)
    {
        if (string.IsNullOrWhiteSpace(nick))
            return string.Empty;

        var text = nick.Normalize(System.Text.NormalizationForm.FormC);
        var buffer = new System.Text.StringBuilder(text.Length);
        var pendingSpace = false;
        foreach (var ch in text)
        {
            // Cf (format) = zero-width space/joiner, BOM, soft hyphen и подобное.
            if (System.Globalization.CharUnicodeInfo.GetUnicodeCategory(ch)
                == System.Globalization.UnicodeCategory.Format)
                continue;

            if (char.IsWhiteSpace(ch))
            {
                // Внутренние пробелы схлопываем в один, по краям не оставляем вовсе.
                pendingSpace = buffer.Length > 0;
                continue;
            }

            if (pendingSpace)
            {
                buffer.Append(' ');
                pendingSpace = false;
            }

            buffer.Append(ch);
        }

        return buffer.ToString();
    }

    /// <summary>Нужны ли права на эту команду.</summary>
    public bool IsAdminOnly(string? command)
    {
        if (string.IsNullOrWhiteSpace(command))
            return false;

        var trimmed = command.Trim().TrimStart('/');
        foreach (var candidate in Admin.AdminOnlyCommands)
        {
            if (string.Equals(candidate?.Trim().TrimStart('/'), trimmed,
                    StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    public string DescribeStartMode() => StartMode switch
    {
        MatchStartMode.Auto =>
            $"auto (старт сам, в каждой команде ≥ {MatchStart.Auto.MinPlayersPerTeam})",
        MatchStartMode.Ready =>
            $"ready (старт по /ready; сброс при выходе: {MatchStart.Ready.ResetOnLeave})",
        _ => "manual (старт только по /start или /set start)",
    };

    /// <summary>Шаблон, который сервер пишет при первом запуске.</summary>
    public const string Template = """
{
  // влалChillow — config.json
  // Применить: перезапуск или команда  config reload
  // // — комментарий.

  // ===== ЗАПУСК =====
  // Только для выделенного сервера.
  // Проба (--client) этот блок не читает.
  "launch": {
    // Доп. аргументы, как будто дописаны после exe.
    // Пример: [ "--verbose", "--no-dashboard" ]
    // --client запрещён: конфиг не переключает сервер в пробу.
    "args": [],

    // Дашборд: true / false / null (null — авто по терминалу)
    "dashboard": null,

    // Название лобби (null — дефолтное)
    "title": null,

    // Подробный лог пакетов
    "verbose": false
  },

  // ===== СЕРВЕР =====
  "server": {
    // Приветствие в чат при входе ("" — не слать)
    "welcomeMessage": "добро пожаловать",

    // Максимум игроков
    "maxMembers": 16,

    // Порт лобби. Меняй, только если понимаешь, что делаешь.
    "lobbyPort": 7778
  },

  // ===== СЕТЬ =====
  "network": {
    // Где открывать сервер:
    //   "0.0.0.0" — все интерфейсы сразу (Wi-Fi, Ethernet, VPN, Hamachi)
    //   ""        — авто-выбор LAN-адреса
    //   "192.168.1.5" — только этот адрес
    "bindAddress": "0.0.0.0",

    // true — не трогать VPN/виртуальные адаптеры (Wi-Fi/Ethernet)
    // false — слушать везде: VPN, Hamachi, Radmin, ZeroTier
    "ignoreVpn": true,

    // IP для op9 (адрес матча). null — авто.
    // Задай вручную, если играете через Hamachi/Radmin.
    "advertiseIp": null,
  },

  // ===== АДМИНЫ =====
  "admin": {
    // Ники как в лобби, регистр не важен. Пусто — админов нет.
    "nicknames": [],

    // Команды только для админов. По умолчанию set и end: они меняют параметры
    // хоста (раунды, победу, состав) и обрывают катку.
    // /mode /map /start /play НЕ входят — запускать матч может кто угодно.
    "adminOnlyCommands": [ "set", "end" ],

    // /start и /set start может любой (даже если set закрыт админом)
    "anyoneCanStart": true,

    // ===== БАНЫ =====
    // /ban <ник> <срок> [причина]   срок: 30m, 6h, 3d (от 10 минут до 14 дней)
    // /unban <ник|IP>               снять бан
    // /bans                         кто забанен и сколько осталось
    // Админов банить нельзя. Проверка на входе в лобби, в матч и на реконнекте.
    "ban": {
      "enabled": true,
      "adminOnly": true,          // false — /ban сможет любой (не советую)
      "file": "banlist.json",    // рядом с config.json; переживает перезапуск
      "minMinutes": 10,
      "maxDays": 14,
      "bindAliases": true,        // новый ник/новый IP дописываются в ту же запись
      "notifyEveryone": true,     // всем в лобби: «X забанен на 30м (причина)»
      "shadowRefreshSec": 60,     // как часто обновлять «Разбан через: …»
      "sweepSec": 5,              // как часто проверять, у кого вышел срок
      "exposeIds": true           // в игре вместо uid «Offline» — серверный id
    }
  },

  // ===== СТАРТ МАТЧА =====
  "matchStart": {
    // manual — по /start, auto — по игрокам, ready — по /ready
    "mode": "auto",

    "auto": {
      // Минимум игроков в каждой команде
      "minPlayersPerTeam": 1
    },

    "ready": {
      "announceInChat": true,
      "resetOnLeave": true,
      "allowUnready": true
    }
  },

  // ===== ТАЙМИНГИ (секунды) =====
  "timings": {
    "warmup": 3,        // WarmUp C2=21
    "preStart": 3,      // PreStart C2=22
    "prep": 10,         // Prep/закупка C2=31
    "roundTime": 109,   // длина раунда (клиент 1:49)
    "roundEndPause": 6, // пауза после RoundEnd
    "bombFuse": 40,     // фитиль бомбы

    "totalRounds": 8,   // MR-N (Escalation)
    "winsNeeded": 0,    // победа до N; 0 — дефолт режима

    "roundStartMoney": 800
  },

  // ===== ПРОИЗВОДИТЕЛЬНОСТЬ =====
  "tuning": {
    "tickMs": 33,            // цикл хоста, мс
    "stateIntervalMs": 50,   // рассылка поз, мс

    // Очередь надёжных пакетов: после неё дропаем только косметику
    "reliableQueueSoftLimit": 256,

    "packetPoolSize": 4096,
    "disconnectTimeoutMs": 10000,
    "netUpdateTimeMs": 15,
    "pingIntervalMs": 1000,

    // Строка [health], сек (0 — выключить)
    "healthIntervalSec": 10
  },

  // ===== ОТЛАДКА =====
  // Только для выделенного сервера.
  // Проба (--client) это не читает.
  "debug": {
    "verbose": false,     // лог каждого пакета
    "captures": false,    // писать capture-файлы
    "capturesDir": null,  // null — captures/ рядом с exe
    "capturesMaxFiles": 4000,
    "capturesMaxBytesMb": 64,

    "matchChat": false,   // отладочные сообщения в чат
    "health": true,       // строка [health]

    "logPackets": false,  // каждый принятый пакет
    "logDiscovery": false // каждый probe
  }
}
""";
}

using System.Net;
using System.Text.Json;

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
public sealed class AdminConfig
{
    /// <summary>Ники администраторов (регистр не важен).</summary>
    public List<string> Nicknames { get; set; } = new();

    /// <summary>Команды только для админов: mode, map, set, play, end, start.</summary>
    public List<string> AdminOnlyCommands { get; set; } =
        new() { "mode", "map", "set", "play", "end", "start" };

    /// <summary>Разрешить /start и /set start любому игроку.</summary>
    public bool AnyoneCanStart { get; set; }
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
    /// <summary>manual | auto | ready</summary>
    public string Mode { get; set; } = "manual";

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
                Console.WriteLine("[config] впиши свои ники в admin.nicknames");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[config] не смог создать '{target}': {ex.Message}");
            }

            Current = new ServerConfig();
            return Current;
        }

        try
        {
            var cfg = JsonSerializer.Deserialize<ServerConfig>(
                File.ReadAllText(target), JsonOptions) ?? new ServerConfig();
            cfg.Normalize();
            Current = cfg;
            Console.WriteLine($"[config] загружен: {target}");
            return cfg;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[config] ошибка чтения '{target}': {ex.Message}");
            Console.WriteLine("[config] работаю на дефолтах — файл НЕ изменён");
            Current = new ServerConfig();
            return Current;
        }
    }

    /// <summary>Приводит значения к безопасным.</summary>
    public void Normalize()
    {
        Launch ??= new LaunchConfig();
        Launch.Args ??= new List<string>();
        Server ??= new ServerSectionConfig();
        Network ??= new NetworkConfig();
        Admin ??= new AdminConfig();
        Admin.Nicknames ??= new List<string>();
        Admin.AdminOnlyCommands ??= new List<string>();
        MatchStart ??= new MatchStartConfig();
        MatchStart.Auto ??= new AutoStartConfig();
        MatchStart.Ready ??= new ReadyStartConfig();
        Timings ??= new TimingsConfig();
        Tuning ??= new TuningConfig();
        Debug ??= new DebugConfig();

        // Прова в пробу конфигом запрещена — иначе весь блок debug «утечёт» в пробу.
        Launch.Args.RemoveAll(a =>
            a is "--client" or "-c" or "connectasclient" or "--connectasclient");

        if (MatchStart.Auto.MinPlayersPerTeam < 1)
            MatchStart.Auto.MinPlayersPerTeam = 1;

        if (!TryParseMode(MatchStart.Mode, out _))
            MatchStart.Mode = "manual";

        Server.MaxMembers = Clamp(Server.MaxMembers, 1, 255, 16);
        Server.LobbyPort = Clamp(Server.LobbyPort, 1, 65535, 7778);

        // Пустой/битый bindAddress — значит «как раньше»: 0.0.0.0 (все интерфейсы).
        if (string.IsNullOrWhiteSpace(Network.BindAddress))
            Network.BindAddress = "0.0.0.0";
        if (!IPAddress.TryParse(Network.BindAddress.Trim(), out _))
            Network.BindAddress = "0.0.0.0";

        if (!string.IsNullOrWhiteSpace(Network.AdvertiseIp)
            && !IPAddress.TryParse(Network.AdvertiseIp.Trim(), out _))
            Network.AdvertiseIp = null;

        Timings.Warmup = Clamp(Timings.Warmup, 0, 120, 3);
        Timings.PreStart = Clamp(Timings.PreStart, 1, 120, 3);
        Timings.Prep = Clamp(Timings.Prep, 1, 120, 10);
        Timings.RoundTime = Clamp(Timings.RoundTime, 10, 600, 109);
        Timings.RoundEndPause = Clamp(Timings.RoundEndPause, 1, 30, 6);
        Timings.BombFuse = Clamp(Timings.BombFuse, 5, 120, 40);
        Timings.TotalRounds = Clamp(Timings.TotalRounds, 1, 64, 8);
        Timings.WinsNeeded = Clamp(Timings.WinsNeeded, 0, 64, 0);
        Timings.RoundStartMoney = Clamp(Timings.RoundStartMoney, 0, 16000, 800);

        Tuning.TickMs = Clamp(Tuning.TickMs, 5, 200, 33);
        Tuning.StateIntervalMs = Clamp(Tuning.StateIntervalMs, 10, 500, 50);
        Tuning.ReliableQueueSoftLimit = Clamp(Tuning.ReliableQueueSoftLimit, 16, 4096, 256);
        Tuning.PacketPoolSize = Clamp(Tuning.PacketPoolSize, 64, 65536, 4096);
        Tuning.DisconnectTimeoutMs = Clamp(Tuning.DisconnectTimeoutMs, 2000, 60000, 10000);
        Tuning.NetUpdateTimeMs = Clamp(Tuning.NetUpdateTimeMs, 1, 100, 15);
        Tuning.PingIntervalMs = Clamp(Tuning.PingIntervalMs, 100, 10000, 1000);
        Tuning.HealthIntervalSec = Clamp(Tuning.HealthIntervalSec, 0, 3600, 10);

        Debug.CapturesMaxFiles = Clamp(Debug.CapturesMaxFiles, 0, 1_000_000, 4000);
        Debug.CapturesMaxBytesMb = Clamp(Debug.CapturesMaxBytesMb, 0, 65536, 64);
    }

    private static int Clamp(int value, int min, int max, int fallback) =>
        value < min || value > max ? fallback : value;

    public MatchStartMode StartMode => TryParseMode(MatchStart.Mode, out var mode)
        ? mode
        : MatchStartMode.Manual;

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

    /// <summary>Админ ли ник (без учёта регистра и пробелов).</summary>
    public bool IsAdmin(string? nick)
    {
        if (string.IsNullOrWhiteSpace(nick))
            return false;

        var trimmed = nick.Trim();
        foreach (var candidate in Admin.Nicknames)
        {
            if (string.Equals(candidate?.Trim(), trimmed, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    /// <summary>Нужны ли права на эту команду.</summary>
    public bool IsAdminOnly(string? command)
    {
        if (string.IsNullOrWhiteSpace(command))
            return false;

        var trimmed = command.Trim();
        foreach (var candidate in Admin.AdminOnlyCommands)
        {
            if (string.Equals(candidate?.Trim(), trimmed, StringComparison.OrdinalIgnoreCase))
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
    // Ники как в лобби, регистр не важен
    "nicknames": [ "влал" ],

    // Команды только для админов
    "adminOnlyCommands": [ "mode", "map", "set", "play", "end", "start" ],

    // /start и /set start может любой
    "anyoneCanStart": false
  },

  // ===== СТАРТ МАТЧА =====
  "matchStart": {
    // manual — по /start, auto — по игрокам, ready — по /ready
    "mode": "manual",

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

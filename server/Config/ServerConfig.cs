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
    /// <summary>Ники администраторов — как игрок зашёл в лобби. Регистр не важен.</summary>
    public List<string> Nicknames { get; set; } = new();

    /// <summary>
    /// Команды, доступные только администраторам.
    /// Допустимые значения: <c>mode</c>, <c>map</c>, <c>set</c>, <c>play</c>, <c>end</c>, <c>start</c>.
    /// </summary>
    public List<string> AdminOnlyCommands { get; set; } =
        new() { "mode", "map", "set", "play", "end", "start" };

    /// <summary>
    /// true — <c>/start</c> и <c>/set start</c> может писать кто угодно, не только админ.
    /// Перекрывает <c>adminOnlyCommands</c> для команды <c>start</c>.
    /// </summary>
    public bool AnyoneCanStart { get; set; }
}

/// <summary>Настройки автостарта (<c>matchStart.mode = "auto"</c>).</summary>
public sealed class AutoStartConfig
{
    /// <summary>Минимум живых игроков в КАЖДОЙ команде (и CT, и TR) для автозапуска.</summary>
    public int MinPlayersPerTeam { get; set; } = 1;
}

/// <summary>Настройки старта по готовности (<c>matchStart.mode = "ready"</c>).</summary>
public sealed class ReadyStartConfig
{
    /// <summary>Писать в чат «ник готов (1/3)» после каждого <c>/ready</c>.</summary>
    public bool AnnounceInChat { get; set; } = true;

    /// <summary>
    /// Сбрасывать голос игрока, когда он вышел из катки. Иначе вышедший
    /// навсегда блокировал бы старт.
    /// </summary>
    public bool ResetOnLeave { get; set; } = true;

    /// <summary>Разрешить <c>/unready</c> — отменить свою готовность.</summary>
    public bool AllowUnready { get; set; } = true;
}

/// <summary>Как начинается матч.</summary>
public sealed class MatchStartConfig
{
    /// <summary>Одно из: <c>manual</c>, <c>auto</c>, <c>ready</c>.</summary>
    public string Mode { get; set; } = "manual";

    public AutoStartConfig Auto { get; set; } = new();

    public ReadyStartConfig Ready { get; set; } = new();
}

/// <summary>
/// Корневой конфиг сервера. Читается из <c>config.json</c> рядом с бинарником;
/// если файла нет — создаётся закомментированный шаблон со значениями по умолчанию.
/// </summary>
public sealed class ServerConfig
{
    public const string FileName = "config.json";

    public AdminConfig Admin { get; set; } = new();

    public MatchStartConfig MatchStart { get; set; } = new();

    /// <summary>Путь, из которого конфиг был загружен в последний раз.</summary>
    public static string LastLoadedPath { get; private set; } = DefaultPath;

    /// <summary>Последний успешно загруженный конфиг (для консольных команд).</summary>
    public static ServerConfig Current { get; private set; } = new();

    public static string DefaultPath => Path.Combine(AppContext.BaseDirectory, FileName);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        // Шаблон пишется с пояснениями на // — System.Text.Json их пропускает.
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
    };

    /// <summary>Загружает конфиг, при отсутствии файла создаёт шаблон. Никогда не бросает.</summary>
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
                Console.WriteLine($"[config] создан конфиг по умолчанию: {target}");
                Console.WriteLine("[config] открой его и пропиши свои ники в admin.nicknames");
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
            Console.WriteLine("[config] работаю на значениях по умолчанию — файл НЕ изменён");
            Current = new ServerConfig();
            return Current;
        }
    }

    /// <summary>Приводит прочитанное к безопасным значениям (пустые списки, минимумы, режим).</summary>
    public void Normalize()
    {
        Admin.Nicknames ??= new List<string>();
        Admin.AdminOnlyCommands ??= new List<string>();
        MatchStart.Auto ??= new AutoStartConfig();
        MatchStart.Ready ??= new ReadyStartConfig();

        if (MatchStart.Auto.MinPlayersPerTeam < 1)
            MatchStart.Auto.MinPlayersPerTeam = 1;

        if (!TryParseMode(MatchStart.Mode, out _))
            MatchStart.Mode = "manual";
    }

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

    /// <summary>Админ ли это (сравнение ников без учёта регистра).</summary>
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

    /// <summary>Нужны ли права администратора на эту команду.</summary>
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
            $"auto (старт сам, когда в каждой команде ≥ {MatchStart.Auto.MinPlayersPerTeam})",
        MatchStartMode.Ready =>
            $"ready (старт, когда все в катке написали /ready; сброс при выходе: {MatchStart.Ready.ResetOnLeave})",
        _ => "manual (старт только по /start или /set start)",
    };

    /// <summary>Закомментированный шаблон, который сервер пишет при первом запуске.</summary>
    public const string Template = """
{
  // ==========================================================================
  //  влалChillow — конфиг выделенного LAN-сервера
  //
  //  Этот файл создан автоматически при первом запуске.
  //  Строки, начинающиеся на // — комментарии, сервер их игнорирует.
  //
  //  Лежит здесь:  рядом с StandChillow.LanServer.exe  (config.json)
  //  Как применить: правишь → перезапускаешь сервер
  //                 (или пишешь в консоль сервера:  config reload)
  // ==========================================================================

  // ---------- КТО АДМИН ----------------------------------------------------
  "admin": {
    // Ники администраторов — точь-в-точь как игрок зашёл в лобби.
    // Регистр не важен. Пустой список = админов нет, их команды никому недоступны.
    "nicknames": [ "влал" ],

    // Команды, доступные ТОЛЬКО админам из списка выше.
    // Убери команду из списка — и её сможет писать кто угодно.
    // Возможные значения: "mode", "map", "set", "play", "end", "start"
    "adminOnlyCommands": [ "mode", "map", "set", "play", "end", "start" ],

    // Разрешить ЛЮБОМУ игроку запускать матч — /start или /set start.
    //   false — запускают только админы
    //   true  — запустить может кто угодно
    // Имеет смысл только если "start" есть в adminOnlyCommands выше.
    "anyoneCanStart": false
  },

  // ---------- КАК НАЧИНАЕТСЯ МАТЧ ------------------------------------------
  "matchStart": {
    // Режим старта. Ровно одно из трёх значений:
    //
    //   "manual"  по-старому: старт только по команде /start или /set start.
    //             Кто имеет право на команду — решается блоком admin выше.
    //
    //   "auto"    сервер стартует САМ, как только в каждой команде набралось
    //             минимум игроков (ниже: auto.minPlayersPerTeam).
    //             Делает ровно то же, что ручной /set start.
    //
    //   "ready"   каждый, кто в катке, пишет /ready. Когда готовы ВСЕ, кто
    //             сейчас в матче — сервер стартует сам.
    //             Игрок вышел из катки — его голос выбрасывается, счётчик
    //             пересчитывается. Из лобби /ready написать нельзя.
    //
    // Одновременно активен только один режим.
    "mode": "manual",

    // Настройки режима "auto"
    "auto": {
      // Сколько игроков минимум должно быть в КАЖДОЙ команде (и CT, и TR),
      // чтобы сервер стартовал сам.
      //   1 = как только в обеих командах есть хотя бы по одному игроку
      //   2 = ждём по два в каждой команде, и так далее
      "minPlayersPerTeam": 1
    },

    // Настройки режима "ready"
    "ready": {
      // Писать в чат «влал готов (1/3)» после каждого /ready
      "announceInChat": true,

      // Сбрасывать голос игрока, когда он вышел из катки.
      // Иначе вышедший игрок навсегда заблокировал бы старт.
      "resetOnLeave": true,

      // Разрешить /unready — отменить свою готовность
      "allowUnready": true
    }
  }
}
""";
}

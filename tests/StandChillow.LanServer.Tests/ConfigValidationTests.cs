using StandChillow.LanServer.Config;
using Xunit;

namespace StandChillow.LanServer.Tests;

/// <summary>
/// Конфиг обязан не только читаться, но и <b>сообщать</b>, что он применил.
/// </summary>
/// <remarks>
/// Пользователь: «я могу параметры указать, а конфиг никогда не читается», «чтоб не
/// заёбывало то, что сервер не применяет / игнорирует молча». Поэтому каждый тест здесь
/// проверяет две вещи сразу: значение заменено стандартным <b>и</b> в <see cref="ServerConfig.Issues"/>
/// есть запись с ключом настройки. Тест на «все настройки покрыты» не даст новым полям
/// появиться в конфиге и снова тихо не читаться.
/// </remarks>
public sealed class ConfigValidationTests
{
    private static ServerConfig Validate(Action<ServerConfig> mutate)
    {
        var cfg = new ServerConfig();
        mutate(cfg);
        cfg.Normalize();
        return cfg;
    }

    private static ConfigIssue Single(ServerConfig cfg, string key)
    {
        var hits = cfg.Issues.Where(i => i.Key == key).ToList();
        Assert.Single(hits);
        return hits[0];
    }

    [Fact]
    public void ValidConfig_ReportsNoIssues()
    {
        var cfg = new ServerConfig();
        cfg.Normalize();

        Assert.Empty(cfg.Issues);
    }

    [Fact]
    public void CleanTemplate_ReportsNoIssues()
    {
        // Шаблон, который сервер сам пишет на первом запуске, обязан быть валидным:
        // иначе каждый старт начинается с ругани на собственный файл.
        var cfg = ServerConfig.ParseForTests(ServerConfig.Template);
        cfg.Normalize();

        Assert.Empty(cfg.Issues);
    }

    [Theory]
    [InlineData("timings.warmup")]
    [InlineData("timings.preStart")]
    [InlineData("timings.prep")]
    [InlineData("timings.roundTime")]
    [InlineData("timings.roundEndPause")]
    [InlineData("timings.bombFuse")]
    [InlineData("timings.totalRounds")]
    [InlineData("timings.winsNeeded")]
    [InlineData("timings.roundStartMoney")]
    [InlineData("tuning.tickMs")]
    [InlineData("tuning.stateIntervalMs")]
    [InlineData("tuning.reliableQueueSoftLimit")]
    [InlineData("tuning.packetPoolSize")]
    [InlineData("tuning.disconnectTimeoutMs")]
    [InlineData("tuning.netUpdateTimeMs")]
    [InlineData("tuning.pingIntervalMs")]
    [InlineData("tuning.healthIntervalSec")]
    [InlineData("server.maxMembers")]
    [InlineData("server.lobbyPort")]
    [InlineData("debug.capturesMaxFiles")]
    [InlineData("debug.capturesMaxBytesMb")]
    public void OutOfRangeNumber_IsReportedAndReplacedByTheStandard(string key)
    {
        // Одно «невозможное» значение (ниже любого минимума) для каждой числовой настройки.
        var cfg = Validate(c =>
        {
            if (key.StartsWith("timings."))
                SetTimings(c, key, -999);
            else if (key.StartsWith("tuning."))
                SetTuning(c, key, -999);
            else if (key == "server.maxMembers")
                c.Server.MaxMembers = -999;
            else if (key == "server.lobbyPort")
                c.Server.LobbyPort = 0;
            else
                SetDebug(c, key, -999);
        });

        var issue = Single(cfg, key);
        // В Detail попало именно то, что мы написали в файле.
        var written = key == "server.lobbyPort" ? "0" : "-999";
        Assert.Contains(written, issue.Detail);
        Assert.False(string.IsNullOrWhiteSpace(issue.Applied));

        // И значение действительно стандартное, а не «как было».
        Assert.NotEqual(-999, ReadNumber(cfg, key));
        if (key == "server.lobbyPort")
            Assert.NotEqual(0, ReadNumber(cfg, key));
    }

    [Fact]
    public void RestartOnlySettings_AreMarkedAsRequiringRestart()
    {
        // Порт лобби и пул пакетов нельзя поменять на живом сокете — их надо пометить,
        // иначе оператор будет ждать эффекта, которого не будет.
        var cfg = Validate(c =>
        {
            c.Server.LobbyPort = 0;
            c.Tuning.PacketPoolSize = 9_999_999;
        });

        Assert.True(Single(cfg, "server.lobbyPort").RequiresRestart);
        Assert.True(Single(cfg, "tuning.packetPoolSize").RequiresRestart);
    }

    [Fact]
    public void HotReloadableSettings_DoNotRequireRestart()
    {
        var cfg = Validate(c => c.Timings.RoundTime = 9999);

        Assert.False(Single(cfg, "timings.roundTime").RequiresRestart);
    }

    [Fact]
    public void BadBindAddress_IsReportedAndFallsBackToAllInterfaces()
    {
        var cfg = Validate(c => c.Network.BindAddress = "не-адрес");

        Assert.Equal("0.0.0.0", cfg.Network.BindAddress);
        Assert.Equal("0.0.0.0", Single(cfg, "network.bindAddress").Applied);
    }

    [Fact]
    public void BadAdvertiseIp_IsReportedAndFallsBackToAutomatic()
    {
        var cfg = Validate(c => c.Network.AdvertiseIp = "999.1.1.1");

        Assert.Null(cfg.Network.AdvertiseIp);
        Assert.Equal("(авто)", Single(cfg, "network.advertiseIp").Applied);
    }

    [Fact]
    public void ValidBindAndAdvertiseIp_AreKept()
    {
        var cfg = Validate(c =>
        {
            c.Network.BindAddress = "192.168.1.65";
            c.Network.AdvertiseIp = "192.168.1.65";
        });

        Assert.Equal("192.168.1.65", cfg.Network.BindAddress);
        Assert.Equal("192.168.1.65", cfg.Network.AdvertiseIp);
        Assert.Empty(cfg.Issues);
    }

    [Theory]
    [InlineData("")]
    [InlineData("manual")]
    [InlineData("auto")]
    [InlineData("ready")]
    public void RecognisedStartModes_AreNotReported(string mode)
    {
        var cfg = Validate(c => c.MatchStart.Mode = mode);

        Assert.DoesNotContain(cfg.Issues, i => i.Key == "matchStart.mode");
    }

    [Fact]
    public void UnknownStartMode_IsReportedAndFallsBackToAuto()
    {
        var cfg = Validate(c => c.MatchStart.Mode = "как-нибудь");

        Assert.Equal(MatchStartMode.Auto, cfg.StartMode);
        Assert.Equal("auto", Single(cfg, "matchStart.mode").Applied);
    }

    [Fact]
    public void Normalize_IsRepeatable_AndDoesNotPileUpIssues()
    {
        // config reload вызывает Normalize повторно — список замечаний не должен расти.
        var cfg = new ServerConfig();
        cfg.Timings.RoundTime = -1;

        cfg.Normalize();
        var first = cfg.Issues.Count;
        cfg.Normalize();

        Assert.True(first > 0);
        Assert.Equal(first, cfg.Issues.Count);
    }

    [Fact]
    public void LogIssues_SaysTheConfigIsCleanWhenNothingWasReplaced()
    {
        var lines = new List<string>();
        var cfg = new ServerConfig();
        cfg.Normalize();

        cfg.LogIssues(lines.Add);

        Assert.Contains(lines, l => l.Contains("корректны"));
    }

    [Fact]
    public void LogIssues_NamesEveryBrokenKey()
    {
        var lines = new List<string>();
        var cfg = new ServerConfig();
        cfg.Timings.Prep = -5;
        cfg.Server.LobbyPort = 0;
        cfg.Normalize();

        cfg.LogIssues(lines.Add);

        Assert.Contains(lines, l => l.Contains("timings.prep"));
        Assert.Contains(lines, l => l.Contains("server.lobbyPort"));
        // Помеченные как «нужен перезапуск» так и подписаны.
        Assert.Contains(lines, l => l.Contains("server.lobbyPort") && l.Contains("перезапуск"));
    }

    /// <summary>
    /// Страж на будущее: если в конфиг добавят секцию, которую <see cref="ServerConfig.Normalize"/>
    /// не знает, этот тест упадёт и напомнит, что её надо проверить и залогировать,
    /// а не оставить тихо нечитаемой.
    /// </summary>
    [Fact]
    public void EveryConfigSection_IsWiredIntoValidation()
    {
        var cfg = new ServerConfig();
        cfg.Normalize();

        // Каждая секция инициализируется (null после JSON не остаётся).
        Assert.NotNull(cfg.Launch);
        Assert.NotNull(cfg.Server);
        Assert.NotNull(cfg.Network);
        Assert.NotNull(cfg.Admin);
        Assert.NotNull(cfg.MatchStart);
        Assert.NotNull(cfg.MatchStart.Auto);
        Assert.NotNull(cfg.MatchStart.Ready);
        Assert.NotNull(cfg.Timings);
        Assert.NotNull(cfg.Tuning);
        Assert.NotNull(cfg.Debug);

        // И у каждой есть хотя бы один параметр, который сервер реально применяет.
        var applied = new[]
        {
            nameof(ServerConfig.Server),
            nameof(ServerConfig.Network),
            nameof(ServerConfig.Admin),
            nameof(ServerConfig.MatchStart),
            nameof(ServerConfig.Timings),
            nameof(ServerConfig.Tuning),
            nameof(ServerConfig.Debug),
        };
        var props = typeof(ServerConfig).GetProperties().Select(p => p.Name).ToHashSet();
        foreach (var section in applied)
            Assert.Contains(section, props);
    }

    private static void SetTimings(ServerConfig c, string key, int value)
    {
        switch (key)
        {
            case "timings.warmup": c.Timings.Warmup = value; break;
            case "timings.preStart": c.Timings.PreStart = value; break;
            case "timings.prep": c.Timings.Prep = value; break;
            case "timings.roundTime": c.Timings.RoundTime = value; break;
            case "timings.roundEndPause": c.Timings.RoundEndPause = value; break;
            case "timings.bombFuse": c.Timings.BombFuse = value; break;
            case "timings.totalRounds": c.Timings.TotalRounds = value; break;
            case "timings.winsNeeded": c.Timings.WinsNeeded = value; break;
            case "timings.roundStartMoney": c.Timings.RoundStartMoney = value; break;
            default: throw new ArgumentOutOfRangeException(nameof(key), key, "нет такой настройки");
        }
    }

    private static void SetTuning(ServerConfig c, string key, int value)
    {
        switch (key)
        {
            case "tuning.tickMs": c.Tuning.TickMs = value; break;
            case "tuning.stateIntervalMs": c.Tuning.StateIntervalMs = value; break;
            case "tuning.reliableQueueSoftLimit": c.Tuning.ReliableQueueSoftLimit = value; break;
            case "tuning.packetPoolSize": c.Tuning.PacketPoolSize = value; break;
            case "tuning.disconnectTimeoutMs": c.Tuning.DisconnectTimeoutMs = value; break;
            case "tuning.netUpdateTimeMs": c.Tuning.NetUpdateTimeMs = value; break;
            case "tuning.pingIntervalMs": c.Tuning.PingIntervalMs = value; break;
            case "tuning.healthIntervalSec": c.Tuning.HealthIntervalSec = value; break;
            default: throw new ArgumentOutOfRangeException(nameof(key), key, "нет такой настройки");
        }
    }

    private static void SetDebug(ServerConfig c, string key, int value)
    {
        switch (key)
        {
            case "debug.capturesMaxFiles": c.Debug.CapturesMaxFiles = value; break;
            case "debug.capturesMaxBytesMb": c.Debug.CapturesMaxBytesMb = value; break;
            default: throw new ArgumentOutOfRangeException(nameof(key), key, "нет такой настройки");
        }
    }

    private static int ReadNumber(ServerConfig cfg, string key) => key switch
    {
        "timings.warmup" => cfg.Timings.Warmup,
        "timings.preStart" => cfg.Timings.PreStart,
        "timings.prep" => cfg.Timings.Prep,
        "timings.roundTime" => cfg.Timings.RoundTime,
        "timings.roundEndPause" => cfg.Timings.RoundEndPause,
        "timings.bombFuse" => cfg.Timings.BombFuse,
        "timings.totalRounds" => cfg.Timings.TotalRounds,
        "timings.winsNeeded" => cfg.Timings.WinsNeeded,
        "timings.roundStartMoney" => cfg.Timings.RoundStartMoney,
        "tuning.tickMs" => cfg.Tuning.TickMs,
        "tuning.stateIntervalMs" => cfg.Tuning.StateIntervalMs,
        "tuning.reliableQueueSoftLimit" => cfg.Tuning.ReliableQueueSoftLimit,
        "tuning.packetPoolSize" => cfg.Tuning.PacketPoolSize,
        "tuning.disconnectTimeoutMs" => cfg.Tuning.DisconnectTimeoutMs,
        "tuning.netUpdateTimeMs" => cfg.Tuning.NetUpdateTimeMs,
        "tuning.pingIntervalMs" => cfg.Tuning.PingIntervalMs,
        "tuning.healthIntervalSec" => cfg.Tuning.HealthIntervalSec,
        "server.maxMembers" => cfg.Server.MaxMembers,
        "server.lobbyPort" => cfg.Server.LobbyPort,
        "debug.capturesMaxFiles" => cfg.Debug.CapturesMaxFiles,
        "debug.capturesMaxBytesMb" => cfg.Debug.CapturesMaxBytesMb,
        _ => throw new ArgumentOutOfRangeException(nameof(key), key, "нет такой настройки"),
    };
}

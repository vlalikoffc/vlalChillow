using System.Text.Json;
using StandChillow.LanServer.Config;
using Xunit;

namespace StandChillow.LanServer.Tests;

/// <summary>
/// Конфиг сервера: шаблон, который сервер пишет при первом запуске, и правила прав.
/// Сокетов нет — проверяется только разбор и логика доступа.
/// </summary>
public sealed class ServerConfigTests
{
    private static readonly JsonSerializerOptions Options = new()
    {
        // Шаблон закомментирован — сервер обязан его проглатывать.
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        PropertyNameCaseInsensitive = true,
    };

    private static ServerConfig Parse(string json) =>
        JsonSerializer.Deserialize<ServerConfig>(json, Options) ?? new ServerConfig();

    [Fact]
    public void Template_ParsesIntoTheDocumentedDefaults()
    {
        var cfg = Parse(ServerConfig.Template);
        cfg.Normalize();

        Assert.Equal(MatchStartMode.Manual, cfg.StartMode);
        Assert.Equal(1, cfg.MatchStart.Auto.MinPlayersPerTeam);
        Assert.True(cfg.MatchStart.Ready.AnnounceInChat);
        Assert.True(cfg.MatchStart.Ready.ResetOnLeave);
        Assert.True(cfg.MatchStart.Ready.AllowUnready);
        Assert.False(cfg.Admin.AnyoneCanStart);
        Assert.Contains("влал", cfg.Admin.Nicknames);
        Assert.Empty(cfg.Launch.Args);
        Assert.False(cfg.Debug.Captures);
        Assert.False(cfg.Debug.Verbose);
        Assert.True(cfg.Debug.Health);
        Assert.Equal(33, cfg.Tuning.TickMs);
        Assert.Equal(256, cfg.Tuning.ReliableQueueSoftLimit);
        Assert.Equal(109, cfg.Timings.RoundTime);
        // Сеть по умолчанию: слушаем везде, VPN не трогаем.
        Assert.Equal("0.0.0.0", cfg.Network.BindAddress);
        Assert.True(cfg.Network.IgnoreVpn);
        Assert.Null(cfg.Network.AdvertiseIp);
    }

    [Fact]
    public void Template_RequiresStartToBeAdminOnly()
    {
        var cfg = Parse(ServerConfig.Template);
        cfg.Normalize();

        // Иначе любой игрок сможет начать матч, как это было до админки.
        Assert.True(cfg.IsAdminOnly("start"));
        Assert.True(cfg.IsAdminOnly("mode"));
        Assert.True(cfg.IsAdminOnly("play"));
    }

    [Fact]
    public void DefaultConfig_IsManualAndHasNoAdmins()
    {
        var cfg = new ServerConfig();

        Assert.Equal(MatchStartMode.Manual, cfg.StartMode);
        Assert.False(cfg.IsAdmin("кто-угодно"));
        Assert.False(cfg.IsAdmin(null));
        Assert.False(cfg.IsAdmin("   "));
    }

    [Fact]
    public void IsAdmin_IgnoresCaseAndSurroundingSpaces()
    {
        var cfg = new ServerConfig();
        cfg.Admin.Nicknames.Add("  Влал  ");

        Assert.True(cfg.IsAdmin("влал"));
        Assert.True(cfg.IsAdmin("ВЛАЛ"));
        Assert.True(cfg.IsAdmin(" влал "));
        Assert.False(cfg.IsAdmin("влал2"));
    }

    [Fact]
    public void AnyoneCanStart_OverridesTheAdminGateForStart()
    {
        var cfg = new ServerConfig();
        Assert.True(cfg.IsAdminOnly("start"));

        // Флаг из конфига — единственный способ пустить всех к запуску.
        cfg.Admin.AnyoneCanStart = true;
        Assert.True(cfg.Admin.AnyoneCanStart);
    }

    [Theory]
    [InlineData("manual", MatchStartMode.Manual)]
    [InlineData("MANUAL", MatchStartMode.Manual)]
    [InlineData("ручной", MatchStartMode.Manual)]
    [InlineData("auto", MatchStartMode.Auto)]
    [InlineData("авто", MatchStartMode.Auto)]
    [InlineData("ready", MatchStartMode.Ready)]
    [InlineData("готовность", MatchStartMode.Ready)]
    public void TryParseMode_AcceptsEnglishAndRussianAliases(string raw, MatchStartMode expected)
    {
        Assert.True(ServerConfig.TryParseMode(raw, out var mode));
        Assert.Equal(expected, mode);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    [InlineData("когда-нибудь")]
    public void TryParseMode_RejectsUnknownValues(string? raw)
    {
        Assert.False(ServerConfig.TryParseMode(raw, out _));
    }

    [Fact]
    public void Normalize_ClampsMinPlayersPerTeamAndFallsBackToManual()
    {
        var cfg = new ServerConfig
        {
            MatchStart = new MatchStartConfig
            {
                Mode = "что-то не то",
                Auto = new AutoStartConfig { MinPlayersPerTeam = 0 },
            },
        };

        cfg.Normalize();

        Assert.Equal("manual", cfg.MatchStart.Mode);
        Assert.Equal(MatchStartMode.Manual, cfg.StartMode);
        Assert.Equal(1, cfg.MatchStart.Auto.MinPlayersPerTeam);
    }

    [Fact]
    public void LaunchArgs_CannotSwitchDedicatedServerIntoProbeMode()
    {
        // Иначе весь блок debug/launch «утечёт» в пробу: программа ушла бы в --client
        // и сама себя перевела в режим, где конфиг запрещён.
        var cfg = new ServerConfig();
        cfg.Launch.Args.AddRange(new[] { "--client", "-c", "ConnectAsClient", "--verbose" });

        cfg.Normalize();

        Assert.DoesNotContain("--client", cfg.Launch.Args);
        Assert.DoesNotContain("-c", cfg.Launch.Args);
        Assert.Single(cfg.Launch.Args);
        Assert.Equal("--verbose", cfg.Launch.Args[0]);
    }

    [Fact]
    public void Normalize_ClampsTimingAndTuningIntoTheLiveRanges()
    {
        var cfg = new ServerConfig
        {
            Timings = new TimingsConfig
            {
                Warmup = 9999,
                RoundTime = 1,
                TotalRounds = 0,
                WinsNeeded = 999,
                BombFuse = 0,
            },
            Tuning = new TuningConfig
            {
                TickMs = 0,
                StateIntervalMs = 10_000,
                ReliableQueueSoftLimit = 0,
                PacketPoolSize = 1,
                HealthIntervalSec = -5,
            },
        };

        cfg.Normalize();

        Assert.Equal(3, cfg.Timings.Warmup);
        Assert.Equal(109, cfg.Timings.RoundTime);
        Assert.Equal(8, cfg.Timings.TotalRounds);
        Assert.Equal(0, cfg.Timings.WinsNeeded);
        Assert.Equal(40, cfg.Timings.BombFuse);
        Assert.Equal(33, cfg.Tuning.TickMs);
        Assert.Equal(50, cfg.Tuning.StateIntervalMs);
        Assert.Equal(256, cfg.Tuning.ReliableQueueSoftLimit);
        Assert.Equal(4096, cfg.Tuning.PacketPoolSize);
        Assert.Equal(10, cfg.Tuning.HealthIntervalSec);
    }

    [Fact]
    public void DebugSection_HasItsOwnDefaultsIndependentOfProbe()
    {
        // Проба не читает config.json вообще; блок debug — только для дедика.
        var debug = new DebugConfig();

        Assert.False(debug.Captures);
        Assert.False(debug.Verbose);
        Assert.False(debug.LogPackets);
        Assert.True(debug.Health);
        Assert.Equal(4000, debug.CapturesMaxFiles);
        Assert.Equal(64, debug.CapturesMaxBytesMb);
    }

    [Theory]
    [InlineData("0.0.0.0", "0.0.0.0")]
    [InlineData(" 192.168.1.5 ", "192.168.1.5")]
    [InlineData("25.7.7.7", "25.7.7.7")] // Hamachi
    public void Normalize_KeepsAValidBindAddress(string raw, string expected)
    {
        var cfg = new ServerConfig { Network = new NetworkConfig { BindAddress = raw } };
        cfg.Normalize();
        Assert.Equal(expected, cfg.Network.BindAddress);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("не-айпи")]
    [InlineData("999.1.1.1")]
    public void Normalize_FallsBackToAnyBindWhenAddressIsGarbage(string raw)
    {
        var cfg = new ServerConfig { Network = new NetworkConfig { BindAddress = raw } };
        cfg.Normalize();

        // Пустой/битый адрес не должен ронять сервер: слушаем все интерфейсы.
        Assert.Equal("0.0.0.0", cfg.Network.BindAddress);
    }

    [Fact]
    public void Normalize_DropsGarbageAdvertiseIp()
    {
        var cfg = new ServerConfig { Network = new NetworkConfig { AdvertiseIp = "не-айпи" } };
        cfg.Normalize();
        Assert.Null(cfg.Network.AdvertiseIp);
    }

    [Fact]
    public void NetworkConfig_DefaultsLiveEverywhereButIgnoreVpn()
    {
        var net = new NetworkConfig();

        Assert.Equal("0.0.0.0", net.BindAddress); // все интерфейсы, включая VPN
        Assert.True(net.IgnoreVpn);               // но виртуальные адаптеры по умолчанию мимо
        Assert.Null(net.AdvertiseIp);
    }

    [Fact]
    public void ProbeDefaults_DoNotDependOnNetworkConfig()
    {
        // Проба читает LanInterfacePicker, а не конфиг: значения по умолчанию
        // остаются такими, какими были до появления блока network.
        Assert.True(StandChillow.LanServer.Lan.LanInterfacePicker.IgnoreVpn);
    }

    [Fact]
    public void Template_IsValidJsonWithComments()
    {
        // Если шаблон сломается, сервер молча уедет на дефолты — ловим это здесь.
        var cfg = Parse(ServerConfig.Template);
        Assert.NotNull(cfg.Admin);
        Assert.NotNull(cfg.MatchStart);
    }
}

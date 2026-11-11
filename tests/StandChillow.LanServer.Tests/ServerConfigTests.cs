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
    public void Template_IsValidJsonWithComments()
    {
        // Если шаблон сломается, сервер молча уедет на дефолты — ловим это здесь.
        var cfg = Parse(ServerConfig.Template);
        Assert.NotNull(cfg.Admin);
        Assert.NotNull(cfg.MatchStart);
    }
}

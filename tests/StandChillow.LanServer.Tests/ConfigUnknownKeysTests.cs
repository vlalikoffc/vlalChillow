using StandChillow.LanServer.Config;
using Xunit;

namespace StandChillow.LanServer.Tests;

/// <summary>
/// Опечатки в конфиге должны называться вслух.
/// </summary>
/// <remarks>
/// Пользователь: «админы есть, а сервер говорит, что я не админ». Самая частая причина —
/// ключ назван не так (<c>admins</c> в корне, <c>adminNicknames</c> внутри admin): раньше
/// <c>System.Text.Json</c> молча выбрасывал такое поле, и админ-список оставался пустым.
/// </remarks>
[Collection("config-file")]
public sealed class ConfigUnknownKeysTests : IDisposable
{
    private readonly string _dir;

    public ConfigUnknownKeysTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "vlal-unknown-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    private ServerConfig Load(string json)
    {
        var path = Path.Combine(_dir, "config.json");
        File.WriteAllText(path, json);
        Assert.True(ServerConfig.TryLoad(path, out var cfg, out var error), error);
        return cfg;
    }

    [Fact]
    public void RootAdminsKeyIsReportedAndSuggestsAdminNicknames()
    {
        var cfg = Load("""
        {
          "admins": [ "влал" ]
        }
        """);

        var issue = Assert.Single(cfg.Issues.Where(i => i.Key == "admins"));
        Assert.Contains("admin.nicknames", issue.Detail);
        Assert.False(cfg.IsAdmin("влал"));
    }

    [Fact]
    public void MisspelledKeyInsideAdminIsReported()
    {
        var cfg = Load("""
        {
          "admin": {
            "nicknames": [ "влал" ],
            "adminNicknames": [ "влал" ]
          }
        }
        """);

        Assert.True(cfg.IsAdmin("влал"));
        var issue = Assert.Single(cfg.Issues.Where(i => i.Key == "admin.adminNicknames"));
        Assert.Contains("admin", issue.Detail);
    }

    [Fact]
    public void StockTemplateHasNoUnknownKeys()
    {
        var cfg = Load(ServerConfig.Template);

        Assert.DoesNotContain(cfg.Issues, i => i.Detail.Contains("неизвестный ключ"));
    }
}

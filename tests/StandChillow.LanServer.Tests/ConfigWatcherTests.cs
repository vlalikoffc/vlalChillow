using StandChillow.LanServer.Config;
using StandChillow.LanServer.Startup;
using Xunit;

namespace StandChillow.LanServer.Tests;

/// <summary>
/// Конфиг обязан перечитываться сам, когда файл меняется.
/// </summary>
/// <remarks>
/// Пользователь: «сделай чтоб реально конфиг читался», «при любом изменении конфига
/// также настройки применялись». До этого <c>config.json</c> читался один раз на старте,
/// поэтому правки в файле не давали никакого эффекта.
/// </remarks>
[Collection("config-file")]
public sealed class ConfigWatcherTests : IDisposable
{
    private readonly string _dir;
    private readonly string _path;

    public ConfigWatcherTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "vlal-config-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _path = Path.Combine(_dir, "config.json");
        File.WriteAllText(_path, ServerConfig.Template);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    private static bool WaitFor(Func<bool> condition, int timeoutMs = 8000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            if (condition())
                return true;
            Thread.Sleep(50);
        }

        return condition();
    }

    [Fact]
    public void ReloadingExplicitly_AppliesTheNewValuesEvenWithoutFileEvents()
    {
        // На CI-ФС события FileSystemWatcher могут не приходить вовсе. Явный перечит
        // обязан работать всегда — это и есть «при любом изменении применялись».
        File.WriteAllText(_path, ServerConfig.Template.Replace(
            "\"roundTime\": 109", "\"roundTime\": 150"));

        var cfg = ServerConfig.Load(_path);

        Assert.Equal(150, cfg.Timings.RoundTime);
    }

    [Fact]
    public void EditingTheFile_AppliesTheNewValues()
    {
        ServerConfig? applied = null;
        using var watcher = new ConfigWatcher(_path, cfg => applied = cfg, log: _ => { });

        // Меняем длину раунда — она применяется на живом хосте.
        File.WriteAllText(_path, ServerConfig.Template.Replace(
            "\"roundTime\": 109", "\"roundTime\": 150"));

        Assert.True(WaitFor(() => applied is { Timings.RoundTime: 150 }),
            "правка config.json не была применена — перечитывания нет");
    }

    [Fact]
    public void BrokenFile_DoesNotApplyGarbageAndKeepsTheServerAlive()
    {
        var applied = 0;
        using var watcher = new ConfigWatcher(_path, _ => Interlocked.Increment(ref applied), log: _ => { });

        File.WriteAllText(_path, "{ это не json ");
        Thread.Sleep(1200);

        // Ни одного «применения» мусора; сервер не упал.
        Assert.Equal(0, Volatile.Read(ref applied));
    }

    [Fact]
    public void InvalidValueOnReload_IsStillLoadedWithTheStandardValue()
    {
        ServerConfig? applied = null;
        using var watcher = new ConfigWatcher(_path, cfg => applied = cfg, log: _ => { });

        File.WriteAllText(_path, ServerConfig.Template.Replace(
            "\"roundTime\": 109", "\"roundTime\": -5"));

        Assert.True(WaitFor(() => applied is not null));
        // Некорректное значение заменено стандартным и отмечено в Issues,
        // а не применено как -5.
        Assert.Equal(109, applied!.Timings.RoundTime);
        Assert.Contains(applied.Issues, i => i.Key == "timings.roundTime");
    }

    [Fact]
    public void MissingDirectory_ThrowsInsteadOfCrashingSilently()
    {
        var missing = Path.Combine(_dir, "нет-такого-каталога", "config.json");

        Assert.Throws<DirectoryNotFoundException>(() =>
            new ConfigWatcher(missing, _ => { }, log: _ => { }));
    }
}

using System.Text;
using StandChillow.LanServer.Net;
using Xunit;

namespace StandChillow.LanServer.Tests;

/// <summary>
/// Код админа обязан быть доступен без консоли: файл <c>auth.txt</c> рядом с конфигом.
/// </summary>
/// <remarks>
/// Пользователь: «а где код взять??? мог бы хоть в корне рядом с конфигом создать auth.txt,
/// где сам код».
/// </remarks>
public sealed class AdminAuthFileTests : IDisposable
{
    private readonly string _dir;

    public AdminAuthFileTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "vlal-auth-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    [Fact]
    public void FileSitsNextToConfig()
    {
        var config = Path.Combine(_dir, "config.json");

        Assert.Equal(Path.Combine(_dir, "auth.txt"), AdminAuthFile.ResolvePath(config));
    }

    [Fact]
    public void MissingConfigPathFallsBackToTheServerDirectory()
    {
        Assert.Equal(
            Path.Combine(AppContext.BaseDirectory, "auth.txt"),
            AdminAuthFile.ResolvePath(configPath: null));
        Assert.Equal(
            Path.Combine(AppContext.BaseDirectory, "auth.txt"),
            AdminAuthFile.ResolvePath("  "));
    }

    [Fact]
    public void CodeIsWrittenInPlainTextAndIsEasyToFind()
    {
        var path = AdminAuthFile.ResolvePath(Path.Combine(_dir, "config.json"));
        var code = "AbCdEfGh2345JkLmNpQrSt2";

        Assert.True(AdminAuthFile.TryWrite(
            path, code, Path.Combine(_dir, "config.json"), DateTime.UtcNow, out var error), error);

        var text = File.ReadAllText(path);
        Assert.Contains(code, text);
        Assert.Contains("auth reset", text);
        // Без BOM: файл открывают блокнотом, лишние байты в начале только мешают.
        Assert.False(File.ReadAllBytes(path).Take(3).SequenceEqual(new byte[] { 0xEF, 0xBB, 0xBF }));
    }

    [Fact]
    public void SecondRunReplacesTheOldCode()
    {
        var path = AdminAuthFile.ResolvePath(Path.Combine(_dir, "config.json"));
        AdminAuthFile.TryWrite(path, "OLDRUNOLDRUNOLDRUNOLDRU", null, DateTime.UtcNow, out _);
        AdminAuthFile.TryWrite(path, "NEWRUNNEWRUNNEWRUNNEWRU", null, DateTime.UtcNow, out _);

        var text = File.ReadAllText(path);
        Assert.Contains("NEWRUNNEWRUNNEWRUNNEWRU", text);
        Assert.DoesNotContain("OLDRUNOLDRUNOLDRUNOLDRU", text);
    }

    [Fact]
    public void StopRemovesTheFile()
    {
        var path = AdminAuthFile.ResolvePath(Path.Combine(_dir, "config.json"));
        AdminAuthFile.TryWrite(path, "SOMECODESOMECODESOMECOD", null, DateTime.UtcNow, out _);
        Assert.True(File.Exists(path));

        Assert.True(AdminAuthFile.TryDelete(path));
        Assert.False(File.Exists(path));
        Assert.False(AdminAuthFile.TryDelete(path)); // второй раз — уже нет
    }

    [Fact]
    public void UnwritableDirectoryIsReportedInsteadOfThrowing()
    {
        var blocker = Path.Combine(_dir, "blocker");
        File.WriteAllText(blocker, "not a directory");

        var path = Path.Combine(blocker, AdminAuthFile.FileName);
        Assert.False(AdminAuthFile.TryWrite(path, "CODE", null, DateTime.UtcNow, out var error));
        Assert.False(string.IsNullOrWhiteSpace(error));
    }

    [Fact]
    public void ContentIsUtf8WithoutBom()
    {
        var content = AdminAuthFile.BuildContent("КОД123", "C:\\dir\\config.json", DateTime.UnixEpoch);

        var bytes = new UTF8Encoding(false).GetBytes(content);
        Assert.Equal(bytes.Length, Encoding.UTF8.GetByteCount(content));
        Assert.Contains("C:\\dir\\config.json", content);
    }
}

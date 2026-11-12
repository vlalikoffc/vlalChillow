using System.Threading;
using StandChillow.LanServer.Config;

namespace StandChillow.LanServer.Startup;

/// <summary>
/// Следит за <c>config.json</c> и перечитывает его при изменении — без перезапуска сервера.
/// </summary>
/// <remarks>
/// До этого конфиг читался ровно один раз в <c>Program.cs</c> на старте: правишь файл —
/// сервер продолжает работать со старыми значениями, и выглядит это как «конфиг вообще
/// не читается». Ручной <c>config reload</c> в дашборде был, но про него надо знать.
/// </remarks>
public sealed class ConfigWatcher : IDisposable
{
    /// <summary>
    /// Файл на Windows часто пишется «не атомарно» (редактор сохраняет, потом дописывает),
    /// поэтому изменения склеиваются: ждём затишья, иначе перечитаем недописанный JSON.
    /// </summary>
    private static readonly TimeSpan Debounce = TimeSpan.FromMilliseconds(400);

    private readonly FileSystemWatcher _watcher;
    private readonly Action<ServerConfig> _apply;
    private readonly Action<string> _log;
    private readonly Timer _debounceTimer;
    private readonly object _gate = new();

    private int _disposed;
    private string _lastContentHash = string.Empty;
    private DateTime _lastAppliedUtc = DateTime.MinValue;

    public ConfigWatcher(string path, Action<ServerConfig> apply, Action<string>? log = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        _apply = apply ?? throw new ArgumentNullException(nameof(apply));
        _log = log ?? Console.WriteLine;
        Path = path;

        _debounceTimer = new Timer(_ => Flush(), null, Timeout.Infinite, Timeout.Infinite);

        var dir = System.IO.Path.GetDirectoryName(path);
        if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir))
            throw new DirectoryNotFoundException($"нет каталога конфига: '{dir}'");

        _watcher = new FileSystemWatcher(dir, System.IO.Path.GetFileName(path))
        {
            NotifyFilter = NotifyFilters.LastWrite
                | NotifyFilters.Size
                | NotifyFilters.FileName
                | NotifyFilters.CreationTime,
            // Редакторы сохраняют через rename — иначе событие не придёт вообще.
            EnableRaisingEvents = true,
        };

        _watcher.Changed += OnTouched;
        _watcher.Created += OnTouched;
        _watcher.Renamed += OnTouched;
        _watcher.Deleted += OnTouched;

        // Ошибка наблюдения (например, каталог удалили) не должна убивать сервер.
        _watcher.Error += (_, e) =>
            _log($"[config] наблюдение за файлом сломалось: {e.GetException().Message}");

        _log($"[config] слежу за изменениями: {path}");
    }

    /// <summary>Файл, за которым следим.</summary>
    public string Path { get; }

    private void OnTouched(object sender, FileSystemEventArgs e)
    {
        if (Volatile.Read(ref _disposed) != 0)
            return;

        try
        {
            // Перезапускаем окно тишины на каждое событие — перечитываем один раз,
            // когда файл перестал меняться.
            _debounceTimer.Change(Debounce, Timeout.InfiniteTimeSpan);
        }
        catch (ObjectDisposedException)
        {
            // Гонка с Dispose при выходе — перечитывать уже нечего.
        }
    }

    /// <summary>
    /// Перечитать файл немедленно, не дожидаясь событий ФС.
    /// Нужен и тестам (в контейнерах события могут не приходить), и ручному
    /// <c>config reload</c>, которому нужен тот же путь применения.
    /// </summary>
    public void ReloadNow() => Flush(force: true);

    private void Flush(bool force = false)
    {
        if (Volatile.Read(ref _disposed) != 0)
            return;

        lock (_gate)
        {
            string content;
            try
            {
                if (!File.Exists(Path))
                {
                    _log($"[config] файл исчез: {Path} — работаю на текущих настройках");
                    return;
                }

                content = File.ReadAllText(Path);
            }
            catch (IOException)
            {
                // Ещё пишут — следующее событие нам поможет.
                return;
            }
            catch (UnauthorizedAccessException ex)
            {
                _log($"[config] перечитать не смог: {ex.Message}");
                return;
            }

            // Некоторые редакторы шлют по 2-3 события на одно сохранение.
            if (!force
                && content == _lastContentHash
                && DateTime.UtcNow - _lastAppliedUtc < TimeSpan.FromSeconds(2))
            {
                return;
            }

            _lastContentHash = content;
            _lastAppliedUtc = DateTime.UtcNow;

            // Битый/недописанный файл НЕ применяем: сервер остаётся на прежних
            // настройках. Заменять весь конфиг дефолтами из-за одного кривого
            // сохранения — хуже, чем подождать следующего.
            if (!ServerConfig.TryLoad(Path, out var config, out var error))
            {
                _log($"[config] изменение не применено ({error}) — работаю на прежних настройках");
                return;
            }

            _log($"[config] изменение обнаружено — применил: {Path}");
            _apply(config);
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        try { _watcher.EnableRaisingEvents = false; } catch { /* already gone */ }
        _watcher.Dispose();
        _debounceTimer.Dispose();
    }
}

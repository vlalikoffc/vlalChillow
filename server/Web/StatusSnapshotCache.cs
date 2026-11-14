namespace StandChillow.LanServer.Web;

/// <summary>
/// Кэш сводки для панели: HTTP-поток никогда не трогает горячие блокировки игрового пути.
/// </summary>
/// <remarks>
/// <para>
/// Снимок комнат считается не чаще, чем раз в <see cref="RefreshInterval"/>, а все
/// читатели получают одну и ту же готовую строку JSON. Двадцать открытых вкладок
/// не превращаются в двадцать проходов по реестру за секунду.
/// </para>
/// <para>
/// <see cref="Refresh"/> вызывается фоновым потоком панели; <see cref="Current"/> —
/// обработчиками запросов. Замок держится только на копирование ссылки, поэтому
/// медленный сборщик не блокирует ответы.
/// </para>
/// </remarks>
public sealed class StatusSnapshotCache
{
    /// <summary>Как часто пересчитываем сводку.</summary>
    public static readonly TimeSpan RefreshInterval = TimeSpan.FromSeconds(1);

    private readonly object _gate = new();
    private readonly Func<DateTime> _clock;
    private string _json = "{}";
    private DateTime _builtAtUtc = DateTime.MinValue;

    public StatusSnapshotCache(Func<DateTime>? clock = null)
    {
        _clock = clock ?? (() => DateTime.UtcNow);
    }

    /// <summary>Сколько раз снимок реально пересобирался (диагностика).</summary>
    public long BuildCount { get; private set; }

    /// <summary>Готовая JSON-строка. До первого <see cref="Refresh"/> — пустой объект.</summary>
    public string Current
    {
        get
        {
            lock (_gate)
                return _json;
        }
    }

    /// <summary>
    /// Пересобрать, если с прошлого раза прошла хотя бы <see cref="RefreshInterval"/>.
    /// </summary>
    /// <returns><c>true</c> — снимок пересобран.</returns>
    public bool Refresh(Func<string> build)
    {
        var now = _clock();
        lock (_gate)
        {
            if (now - _builtAtUtc < RefreshInterval)
                return false;
        }

        // Сборка вне замка: она читает реестр команд, а нам нельзя держать панель
        // на блокировке, пока его читает игровой цикл.
        string json;
        try
        {
            json = build();
        }
        catch (Exception ex)
        {
            json = $$"""{"error":{{Quote(ex.Message)}}}""";
        }

        lock (_gate)
        {
            _json = json;
            _builtAtUtc = now;
            BuildCount++;
            return true;
        }
    }

    /// <summary>Принудительно записать готовое значение (тесты, «сейчас же обнови»).</summary>
    public void Set(string json)
    {
        lock (_gate)
        {
            _json = json;
            _builtAtUtc = _clock();
        }
    }

    private static string Quote(string text) =>
        "\"" + text.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
}

using System.Text;

namespace StandChillow.LanServer.Net;

/// <summary>
/// Код админа в файле <c>auth.txt</c> рядом с <c>config.json</c>.
/// </summary>
/// <remarks>
/// <para>
/// Консоль сервера — не всегда то окно, которое видно: пользователь запускает сервер ярлыком,
/// консоль уезжает за игру, а код нужен прямо сейчас. Поэтому он дублируется в файл рядом с
/// конфигом: тот же каталог, который пользователь и так открывает, чтобы править настройки.
/// </para>
/// <para>
/// Файл перезаписывается на каждом запуске и при <c>auth reset</c>, удаляется при остановке
/// сервера: старый код всё равно мёртв, а «ввожу — не работает» на пустом месте не нужно.
/// </para>
/// </remarks>
public static class AdminAuthFile
{
    /// <summary>Имя файла рядом с конфигом.</summary>
    public const string FileName = "auth.txt";

    /// <summary>
    /// Каталог для файла: где лежит <c>config.json</c>, а если путь неизвестен — рядом с exe.
    /// </summary>
    public static string ResolvePath(string? configPath)
    {
        string directory;
        try
        {
            directory = string.IsNullOrWhiteSpace(configPath)
                ? string.Empty
                : (Path.GetDirectoryName(Path.GetFullPath(configPath)) ?? string.Empty);
        }
        catch (Exception)
        {
            directory = string.Empty; // битый путь — падать из-за этого незачем
        }

        if (directory.Length == 0)
            directory = AppContext.BaseDirectory;

        return Path.Combine(directory, FileName);
    }

    /// <summary>Текст файла: код первой строкой, дальше — что с ним делать.</summary>
    public static string BuildContent(string code, string? configPath, DateTime now)
    {
        var sb = new StringBuilder();
        sb.AppendLine("КОД АДМИНА НА ЭТОТ ЗАПУСК");
        sb.AppendLine();
        sb.AppendLine("    " + code);
        sb.AppendLine();
        sb.AppendLine("Что делать:");
        sb.AppendLine("  1. Зайди в лобби под своим ником из admin.nicknames.");
        sb.AppendLine("  2. Сервер напишет в чат, что нужен код.");
        sb.AppendLine("  3. Напиши код в чат — просто " + code + " или /auth " + code);
        sb.AppendLine("  4. Сервер ответит «код принят» и отключит тебя: зайди в лобби заново.");
        sb.AppendLine();
        sb.AppendLine("После этого твой IP авторизован до перезапуска сервера.");
        sb.AppendLine("Код никому не показывай: кто его ввёл, тот и админ.");
        sb.AppendLine("Нужен новый код — перезапусти сервер или набери в консоли: auth reset");
        sb.AppendLine();
        sb.AppendLine("Обновлён: " + now.ToString("yyyy-MM-dd HH:mm:ss"));
        sb.AppendLine("Конфиг:   " + (string.IsNullOrWhiteSpace(configPath) ? "(путь неизвестен)" : configPath));
        return sb.ToString();
    }

    /// <summary>Записать код. <c>false</c> — файл недоступен (только чтение, нет прав).</summary>
    public static bool TryWrite(
        string path, string code, string? configPath, DateTime now, out string error)
    {
        error = string.Empty;
        try
        {
            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
                Directory.CreateDirectory(directory);

            File.WriteAllText(path, BuildContent(code, configPath, now), new UTF8Encoding(false));
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    /// <summary>Убрать файл при остановке сервера: код всё равно больше не действует.</summary>
    public static bool TryDelete(string path)
    {
        try
        {
            if (!File.Exists(path))
                return false;
            File.Delete(path);
            return true;
        }
        catch (Exception)
        {
            return false; // занят/нет прав — не повод шуметь при выходе
        }
    }
}

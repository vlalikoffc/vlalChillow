using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Unicode;

namespace StandChillow.LanServer.Web;

/// <summary>
/// Настройки JSON-писателя для всего веб-слоя.
/// </summary>
/// <remarks>
/// <para>
/// По умолчанию <see cref="Utf8JsonWriter"/> экранирует всё не-ASCII
/// в <c>\uXXXX</c>. Для машины это одно и то же, для человека — нет: в панели и в
/// логах «<c>\u0432\u043b\u0430\u043b</c>» вместо «влал» невозможно читать, а ник
/// и название комнаты — именно кириллица.
/// </para>
/// <para>
/// Разрешаем весь Unicode. Это <b>не</b> ослабление безопасности: страница всё равно
/// экранирует текст при вставке в DOM (см. <c>esc()</c> в index.html), а JSON с
/// <c>Content-Type: application/json</c> браузер не исполняет как HTML.
/// </para>
/// </remarks>
internal static class JsonText
{
    /// <summary>Опции писателя: отступов нет, Unicode не экранируется.</summary>
    public static readonly JsonWriterOptions WriterOptions = new()
    {
        Indented = false,
        Encoder = JavaScriptEncoder.Create(UnicodeRanges.All),
    };
}

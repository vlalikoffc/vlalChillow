namespace StandChillow.LanServer.Bans;

/// <summary>
/// Разбор аргументов <c>/ban</c>: <c>&lt;id&gt; &lt;срок&gt; [причина…]</c>.
/// </summary>
/// <remarks>
/// <para>
/// Бан адресуется **только серверным id** — тем числом, что видно в игре и в <c>/ids</c>.
/// Ник для бана больше не принимается: ник присылает клиент, поэтому «забань влала» легко
/// превращалось в бан не того человека (или в травлю по нику). Id детерминирован от ника,
/// так что за одним числом всегда стоит конкретный человек.
/// </para>
/// <para>
/// Причина — весь остаток строки: она не парсится по словам, иначе «удали чит, а то забаню»
/// превратится в «удали».
/// </para>
/// </remarks>
public static class BanCommandLine
{
    public const int MinId = MatchPublicId.Min;
    public const int MaxId = MatchPublicId.Max;

    public static bool TryParse(
        string? args,
        out int id,
        out string durationToken,
        out string reason,
        out string error)
    {
        id = 0;
        durationToken = string.Empty;
        reason = string.Empty;
        error = string.Empty;

        var text = (args ?? string.Empty).Trim();
        if (text.Length == 0)
        {
            error = "формат: /ban <id> <срок> [причина] — например /ban 67776161 6d софт; id смотри в /ids";
            return false;
        }

        var gap = text.IndexOf(' ');
        var idToken = (gap < 0 ? text : text[..gap]).Trim();
        text = gap < 0 ? string.Empty : text[(gap + 1)..].Trim();

        // «#67776161» — так id показывается в /ids и в игре, пусть можно копировать как есть.
        var digits = idToken.StartsWith('#') ? idToken[1..] : idToken;

        if (!int.TryParse(digits, out id))
        {
            error = idToken.Length > 0 && idToken.Length <= 64
                ? $"'{idToken}' — это не id. Баны только по серверному id: /ban 67776161 6d " +
                  "(свой список — /ids, в игре id виден в списке игроков)"
                : "не похоже на id — нужно число от " + MinId + " до " + MaxId + " (список: /ids)";
            return false;
        }

        if (id < MinId || id > MaxId)
        {
            error = $"id {id} вне диапазона {MinId}…{MaxId} (список: /ids)";
            return false;
        }

        if (text.Length == 0)
        {
            error = "не указан срок: /ban <id> 30m [причина]";
            return false;
        }

        var space = text.IndexOf(' ');
        durationToken = (space < 0 ? text : text[..space]).Trim();
        reason = space < 0 ? string.Empty : text[(space + 1)..].Trim();

        if (durationToken.Length == 0)
        {
            error = "не указан срок: /ban <id> 30m [причина]";
            return false;
        }

        return true;
    }
}

namespace StandChillow.LanServer.Bans;

/// <summary>
/// Разбор <c>/banid &lt;id&gt; &lt;срок&gt; [причина…]</c>.
/// </summary>
/// <remarks>
/// Id — серверный, диапазон тот же, что у <see cref="MatchPublicId"/>; клиент его не
/// присылает. Причина, как и в <see cref="BanCommandLine"/>, — весь остаток строки.
/// </remarks>
public static class BanIdCommandLine
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
            error = "формат: /banid <id> <срок> [причина] — id смотри в /ids";
            return false;
        }

        var gap = text.IndexOf(' ');
        var idToken = gap < 0 ? text : text[..gap];
        text = gap < 0 ? string.Empty : text[(gap + 1)..].Trim();

        if (!int.TryParse(idToken, out id))
        {
            error = $"'{idToken}' — не id. Нужно число {MinId}…{MaxId} (список: /ids)";
            return false;
        }

        if (id < MinId || id > MaxId)
        {
            error = $"id {id} вне диапазона {MinId}…{MaxId} (список: /ids)";
            return false;
        }

        if (text.Length == 0)
        {
            error = "не указан срок: /banid <id> 30m причина";
            return false;
        }

        var space = text.IndexOf(' ');
        durationToken = (space < 0 ? text : text[..space]).Trim();
        reason = space < 0 ? string.Empty : text[(space + 1)..].Trim();

        if (durationToken.Length == 0)
        {
            error = "не указан срок: /banid <id> 30m причина";
            return false;
        }

        return true;
    }
}

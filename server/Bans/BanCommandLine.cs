namespace StandChillow.LanServer.Bans;

/// <summary>
/// Разбор аргументов <c>/ban</c>: <c>&lt;ник&gt; &lt;срок&gt; [причина…]</c>.
/// </summary>
/// <remarks>
/// Ник можно взять в кавычки, если в нём есть пробелы (<c>/ban "влал chillow" 30m …</c>).
/// Причина — всё, что осталось: она не парсится и не режется по словам, иначе
/// «удали чит, а то забаню» превратится в «удали».
/// </remarks>
public static class BanCommandLine
{
    public static bool TryParse(
        string? args,
        out string nick,
        out string durationToken,
        out string reason,
        out string error)
    {
        nick = string.Empty;
        durationToken = string.Empty;
        reason = string.Empty;
        error = string.Empty;

        var text = (args ?? string.Empty).Trim();
        if (text.Length == 0)
        {
            error = "формат: /ban <ник> <срок> [причина] — например /ban Ktulxy 30m софт";
            return false;
        }

        if (text[0] == '"')
        {
            var close = text.IndexOf('"', 1);
            if (close < 0)
            {
                error = "не закрыта кавычка в нике";
                return false;
            }

            nick = text[1..close].Trim();
            text = text[(close + 1)..].Trim();
            if (text.Length == 0)
            {
                error = "не указан срок: /ban \"ник\" 30m причина";
                return false;
            }
        }
        else
        {
            var space = text.IndexOf(' ');
            nick = space < 0 ? text : text[..space];
            text = space < 0 ? string.Empty : text[(space + 1)..].Trim();
            if (text.Length == 0)
            {
                error = "не указан срок: /ban <ник> 30m причина";
                return false;
            }
        }

        if (nick.Length == 0)
        {
            error = "пустой ник";
            return false;
        }

        // 64 символа — уже не ник, а попытка сломать лог или UI.
        if (nick.Length > 64)
        {
            error = "ник длиннее 64 символов — это не ник";
            return false;
        }

        var gap = text.IndexOf(' ');
        durationToken = (gap < 0 ? text : text[..gap]).Trim();
        reason = gap < 0 ? string.Empty : text[(gap + 1)..].Trim();

        if (durationToken.Length == 0)
        {
            error = "не указан срок: /ban <ник> 30m причина";
            return false;
        }

        return true;
    }
}

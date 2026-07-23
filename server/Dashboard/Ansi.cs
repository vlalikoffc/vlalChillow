using System.Text;

namespace StandChillow.LanServer.Dashboard;

/// <summary>Minimal ANSI/VT helpers for the CLI dashboard (truecolor + screen control).</summary>
internal static class Ansi
{
    private const char Esc = '\x1b';

    public const string Reset = "\x1b[0m";
    public const string Bold = "\x1b[1m";
    public const string Dim = "\x1b[2m";

    // Foreground palette — Standoff 2 vibe: CT cyan, T orange/gold, warm gold money.
    public static readonly string Ct = Fg(94, 202, 245);       // defense — cyan/blue
    public static readonly string CtDim = Fg(58, 132, 162);
    public static readonly string Tr = Fg(242, 172, 72);       // attack — orange/gold
    public static readonly string TrDim = Fg(168, 118, 46);
    public static readonly string Text = Fg(214, 218, 224);
    public static readonly string Neutral = Fg(198, 202, 210);
    public static readonly string Muted = Fg(122, 128, 140);
    public static readonly string Faint = Fg(90, 95, 106);
    public static readonly string Border = Fg(64, 70, 84);
    public static readonly string Money = Fg(214, 182, 96);
    public static readonly string Accent = Fg(122, 214, 140);
    public static readonly string Host = Fg(128, 214, 148);
    public static readonly string Danger = Fg(232, 100, 100);
    public static readonly string Ink = Fg(20, 23, 28);        // dark text for colored tiles

    // Background palette (panel cards / zebra / score tiles / bands).
    public static readonly string BgPanel = Bg(25, 28, 35);
    public static readonly string BgZebra = Bg(31, 35, 43);
    public static readonly string BgBand = Bg(40, 45, 55);
    public static readonly string BgCtTile = Bg(46, 132, 174);
    public static readonly string BgTrTile = Bg(184, 118, 44);
    public static readonly string BgCtBand = Bg(28, 58, 74);
    public static readonly string BgTrBand = Bg(74, 54, 26);

    public static string Fg(int r, int g, int b) => $"{Esc}[38;2;{r};{g};{b}m";
    public static string Bg(int r, int g, int b) => $"{Esc}[48;2;{r};{g};{b}m";

    public const string EnterAltScreen = "\x1b[?1049h";
    public const string LeaveAltScreen = "\x1b[?1049l";
    public const string HideCursor = "\x1b[?25l";
    public const string ShowCursor = "\x1b[?25h";
    public const string CursorHome = "\x1b[H";
    public const string ClearScreen = "\x1b[2J";
    public const string ClearToLineEnd = "\x1b[K";

    /// <summary>Visible length of a string ignoring ANSI SGR escape sequences.</summary>
    public static int VisibleLength(string s)
    {
        var len = 0;
        for (var i = 0; i < s.Length; i++)
        {
            if (s[i] == Esc)
            {
                // Skip a CSI sequence: ESC '[' <params 0x30-0x3F>* <final 0x40-0x7E>.
                i++;
                if (i < s.Length && s[i] == '[')
                    i++;
                while (i < s.Length && !(s[i] >= '@' && s[i] <= '~'))
                    i++;
                continue;
            }
            len++;
        }
        return len;
    }

    /// <summary>Truncate a plain (no-ANSI) string to <paramref name="width"/> visible cells.</summary>
    public static string Truncate(string s, int width)
    {
        if (width <= 0) return "";
        if (s.Length <= width) return s;
        if (width == 1) return "…";
        return s[..(width - 1)] + "…";
    }

    /// <summary>Pad a plain (no-ANSI) string to width, aligned left or right.</summary>
    public static string PadPlain(string plain, int width, bool right)
    {
        plain = Truncate(plain, width);
        var pad = Math.Max(0, width - plain.Length);
        return right ? new string(' ', pad) + plain : plain + new string(' ', pad);
    }

    /// <summary>Right-pad a possibly-colored string to <paramref name="width"/> visible cells.</summary>
    public static string PadVisible(string s, int width)
    {
        var vis = VisibleLength(s);
        return vis >= width ? s : s + new string(' ', width - vis);
    }

    /// <summary>Left-pad (right-align) a possibly-colored string to visible width.</summary>
    public static string PadVisibleLeft(string s, int width)
    {
        var vis = VisibleLength(s);
        return vis >= width ? s : new string(' ', width - vis) + s;
    }

    /// <summary>Center a possibly-colored string within visible width.</summary>
    public static string CenterVisible(string s, int width)
    {
        var vis = VisibleLength(s);
        if (vis >= width) return s;
        var left = (width - vis) / 2;
        var right = width - vis - left;
        return new string(' ', left) + s + new string(' ', right);
    }

    /// <summary>
    /// Apply a background to a line: reapply <paramref name="bg"/> after every <see cref="Reset"/>
    /// so foreground-colored cells keep the panel background, ending with a single reset.
    /// </summary>
    public static string OnBg(string content, string bg) =>
        bg + content.Replace(Reset, Reset + bg) + Reset;
}

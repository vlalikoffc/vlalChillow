namespace StandChillow.LanServer.Dashboard;

/// <summary>
/// VT/ANSI primitives for the dashboard.
/// </summary>
/// <remarks>
/// <b>Why no truecolor.</b> The dashboard used to emit <c>38;2;r;g;b</c> everywhere. Legacy
/// Windows consoles (conhost, <c>cmd.exe</c> on an old Windows 10 build, PowerShell 5 in
/// conhost) do not implement it, and the emoji-ish glyphs used for avatars (U+2588 blocks,
/// U+25C6 diamonds, U+2620 skull) are not in the console font. Result: colour soup, tofu
/// boxes and — once a "wide" glyph was measured as one cell — a layout that looked shifted.
/// This file now only uses the 16 ANSI colours, bold / dim / reverse, and ASCII-or-Latin-1
/// glyphs that every one of cmd, PowerShell and bash can render.
/// </remarks>
internal static class Ansi
{
    private const char Esc = '\x1b';

    public const string Reset = "\x1b[0m";
    public const string Bold = "\x1b[1m";
    public const string Dim = "\x1b[2m";
    public const string NoBold = "\x1b[22m";
    public const string Reverse = "\x1b[7m";
    public const string NoReverse = "\x1b[27m";

    // 16-colour palette (SGR 30-37 / 90-97) — safe in cmd, PowerShell, bash, tmux, IDE consoles.
    public const string Ct = "\x1b[96m";      // defense (CT) — bright cyan
    public const string Tr = "\x1b[93m";      // attack (T) — bright yellow
    public const string Accent = "\x1b[92m";  // ok / online — bright green
    public const string Danger = "\x1b[91m";  // errors / dead — bright red
    public const string Host = "\x1b[92m";    // dedicated host actor
    public const string Money = "\x1b[93m";
    public const string Text = "\x1b[97m";    // primary text (bright white)
    public const string Neutral = "\x1b[37m"; // normal white
    public const string Muted = "\x1b[37m";
    public const string Faint = "\x1b[90m";   // bright black — secondary
    public const string Border = "\x1b[90m";
    public const string Ink = "\x1b[30m";     // text on a reversed (highlighted) cell
    public const string OnCt = "\x1b[46m";    // CT background (cyan)
    public const string OnTr = "\x1b[43m";    // T background (yellow)
    public const string OnBand = "\x1b[100m"; // band background (bright black)

    // Screen control.
    public const string EnterAltScreen = "\x1b[?1049h";
    public const string LeaveAltScreen = "\x1b[?1049l";
    public const string HideCursor = "\x1b[?25l";
    public const string ShowCursor = "\x1b[?25h";
    public const string ClearScreen = "\x1b[2J";
    public const string ClearToLineEnd = "\x1b[K";
    public const string ClearToScreenEnd = "\x1b[J";
    public const string SaveCursor = "\x1b[s";
    public const string RestoreCursor = "\x1b[u";

    /// <summary>Absolute cursor position (1-based row/column) — the dashboard never emits '\n'.</summary>
    public static string MoveTo(int row, int col)
    {
        row = Math.Max(1, row);
        col = Math.Max(1, col);
        return string.Concat(Esc.ToString(), "[", row.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ";", col.ToString(System.Globalization.CultureInfo.InvariantCulture), "H");
    }

    /// <summary>Visible width in terminal cells (kept for callers that only need the measurement).</summary>
    public static int VisibleLength(string? s) => Layout.CellWidth(s);

    public static string Sanitize(string? text) => Layout.SingleLine(text);

    public static string Truncate(string s, int width) => Layout.ClipPlain(s, width);

    /// <summary>True when the payload only contains printable ASCII (tests / log hygiene).</summary>
    public static bool IsPlainAscii(string? s)
    {
        if (string.IsNullOrEmpty(s))
            return true;
        foreach (var c in s)
        {
            if (c < 32 || c > 126)
                return false;
        }

        return true;
    }

    /// <summary>Strips every escape sequence.</summary>
    public static string StripAnsi(string? s) => Layout.StripAnsi(s);
}

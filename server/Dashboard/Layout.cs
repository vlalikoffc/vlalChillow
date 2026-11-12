using System.Globalization;
using System.Text;

namespace StandChillow.LanServer.Dashboard;

/// <summary>
/// Terminal-cell measurement and ANSI-aware padding.
/// </summary>
/// <remarks>
/// The dashboard used to pad with <see cref="string.Length"/>. That is UTF-16 code units, not
/// terminal cells: Cyrillic names, box-drawing characters and CJK/emoji names all broke the
/// column alignment (which is what made the whole screen look shifted). Everything here counts
/// <b>cells</b>: ANSI SGR sequences cost zero, wide East-Asian glyphs cost two, combining marks
/// cost zero.
/// </remarks>
internal static class Layout
{
    public enum Align
    {
        Left,
        Right,
        Center,
    }

    private const char Esc = '\x1b';

    /// <summary>Visible width of a string in terminal cells (ANSI SGR ignored).</summary>
    public static int CellWidth(string? s)
    {
        if (string.IsNullOrEmpty(s))
            return 0;

        var cells = 0;
        for (var i = 0; i < s.Length; i++)
        {
            var c = s[i];
            if (c == Esc)
            {
                i = SkipEscape(s, i);
                continue;
            }

            if (char.IsHighSurrogate(c) && i + 1 < s.Length && char.IsLowSurrogate(s[i + 1]))
            {
                cells += CodePointWidth(char.ConvertToUtf32(c, s[i + 1]));
                i++;
                continue;
            }

            if (char.IsLowSurrogate(c) || c == '\uFE0F' || c == '\u200D' || c == '\uFE0E')
                continue; // orphan surrogate / emoji variation selectors are zero width

            cells += CodePointWidth(c);
        }

        return cells;
    }

    /// <summary>Index of the last character of the escape sequence starting at <paramref name="i"/>.</summary>
    private static int SkipEscape(string s, int i)
    {
        var j = i + 1;
        if (j >= s.Length)
            return s.Length;

        if (s[j] == '[')
        {
            j++;
            while (j < s.Length && !(s[j] >= '@' && s[j] <= '~'))
                j++;
            return Math.Min(j, s.Length - 1);
        }

        if (s[j] == ']')
        {
            // OSC … terminated by BEL or ST (ESC \)
            j++;
            while (j < s.Length && s[j] != '\a')
            {
                if (s[j] == Esc && j + 1 < s.Length && s[j + 1] == '\\')
                    return j + 1;
                j++;
            }

            return Math.Min(j, s.Length - 1);
        }

        // Two-character escapes (ESC 7, ESC M, …) end here; a lone ESC at the end is dropped.
        return Math.Min(j, s.Length - 1);
    }

    /// <summary>Delete every escape sequence (log lines must never carry control codes to the TTY).</summary>
    public static string StripAnsi(string? s)
    {
        if (string.IsNullOrEmpty(s))
            return string.Empty;
        if (s.IndexOf(Esc) < 0)
            return s;

        var sb = new StringBuilder(s.Length);
        for (var i = 0; i < s.Length; i++)
        {
            if (s[i] == Esc)
            {
                i = SkipEscape(s, i);
                continue;
            }

            sb.Append(s[i]);
        }

        return sb.ToString();
    }

    /// <summary>Terminal display width of one code point (East-Asian wide = 2).</summary>
    public static int CodePointWidth(int cp)
    {
        if (cp < 32)
            return 0;
        if (cp < 0x7F)
            return 1;

        // Combining marks / zero-width joiners / variation selectors.
        if (cp is >= 0x0300 and <= 0x036F
            or >= 0x1AB0 and <= 0x1AFF
            or >= 0x1DC0 and <= 0x1DFF
            or >= 0x20D0 and <= 0x20F0
            or >= 0xFE00 and <= 0xFE0F
            or >= 0xFE20 and <= 0xFE2F
            or 0x200B or 0x200C or 0x200D or 0x2060 or 0xFEFF)
            return 0;

        // East-Asian Wide / Fullwidth ranges (CJK, Hangul, kana, fullwidth forms).
        if (cp is >= 0x1100 and <= 0x115F
            or >= 0x2E80 and <= 0x303E
            or >= 0x3041 and <= 0x33FF
            or >= 0x3400 and <= 0x4DBF
            or >= 0x4E00 and <= 0x9FFF
            or >= 0xA000 and <= 0xA4CF
            or >= 0xA960 and <= 0xA97F
            or >= 0xAC00 and <= 0xD7A3
            or >= 0xF900 and <= 0xFAFF
            or >= 0xFE10 and <= 0xFE19
            or >= 0xFE30 and <= 0xFE6F
            or >= 0xFF00 and <= 0xFF60
            or >= 0xFFE0 and <= 0xFFE6
            or >= 0x1F000 and <= 0x1FAFF
            or >= 0x20000 and <= 0x3FFFD)
            return 2;

        return 1;
    }

    /// <summary>Clip a plain string to <paramref name="cells"/> cells, marking the cut.</summary>
    public static string ClipPlain(string plain, int cells)
    {
        if (string.IsNullOrEmpty(plain) || cells <= 0)
            return string.Empty;
        if (CellWidth(plain) <= cells)
            return plain;

        var marker = cells >= 4 ? ".." : "~";
        var budget = Math.Max(0, cells - marker.Length);
        var sb = new StringBuilder(plain.Length);
        var used = 0;
        var i = 0;
        while (i < plain.Length)
        {
            var len = 1;
            int width;
            if (char.IsHighSurrogate(plain[i]) && i + 1 < plain.Length && char.IsLowSurrogate(plain[i + 1]))
            {
                width = CodePointWidth(char.ConvertToUtf32(plain[i], plain[i + 1]));
                len = 2;
            }
            else
            {
                width = CodePointWidth(plain[i]);
            }

            if (used + width > budget)
                break;

            sb.Append(plain, i, len);
            used += width;
            i += len;
        }

        return sb.Append(marker).ToString();
    }

    /// <summary>Clip a possibly-colored string to <paramref name="cells"/> cells.</summary>
    public static string ClipAnsi(string? s, int cells)
    {
        if (string.IsNullOrEmpty(s) || cells <= 0)
            return string.Empty;
        if (CellWidth(s) <= cells)
            return s;

        var marker = cells >= 4 ? ".." : "~";
        var budget = Math.Max(0, cells - marker.Length);
        var sb = new StringBuilder(s.Length);
        var used = 0;
        var sawEscape = false;

        for (var i = 0; i < s.Length; i++)
        {
            var c = s[i];
            if (c == Esc)
            {
                var end = SkipEscape(s, i);
                sb.Append(s, i, end - i + 1);
                sawEscape = true;
                i = end;
                continue;
            }

            var len = 1;
            int width;
            if (char.IsHighSurrogate(c) && i + 1 < s.Length && char.IsLowSurrogate(s[i + 1]))
            {
                width = CodePointWidth(char.ConvertToUtf32(c, s[i + 1]));
                len = 2;
            }
            else
            {
                width = CodePointWidth(c);
            }

            if (used + width > budget)
                break;

            sb.Append(s, i, len);
            used += width;
            i += len - 1;
        }

        sb.Append(marker);
        if (sawEscape)
            sb.Append(Ansi.Reset);
        return sb.ToString();
    }

    /// <summary>Fit a possibly-colored string into exactly <paramref name="cells"/> cells.</summary>
    public static string Fit(string? s, int cells, Align align = Align.Left)
    {
        if (cells <= 0)
            return string.Empty;

        var clipped = ClipAnsi(s, cells);
        var pad = cells - CellWidth(clipped);
        if (pad <= 0)
            return clipped;

        return align switch
        {
            Align.Right => new string(' ', pad) + clipped,
            Align.Center => new string(' ', pad / 2) + clipped + new string(' ', pad - pad / 2),
            _ => clipped + new string(' ', pad),
        };
    }

    /// <summary>Fit a plain (no ANSI) string into exactly <paramref name="cells"/> cells.</summary>
    public static string FitPlain(string? plain, int cells, Align align = Align.Left) =>
        Fit(plain, cells, align);

    /// <summary>Repeat a character to fill <paramref name="cells"/> cells (0 when non-positive).</summary>
    public static string Bar(int cells, char ch) =>
        cells <= 0 ? string.Empty : new string(ch, cells);

    /// <summary>
    /// Make user-supplied text terminal-safe: no newlines, no tabs, no control codes.
    /// Chat bodies and log lines come from phones; a single <c>\n</c> used to shift the whole
    /// dashboard by one row.
    /// </summary>
    public static string SingleLine(string? text)
    {
        if (string.IsNullOrEmpty(text))
            return string.Empty;

        var stripped = StripAnsi(text);
        var sb = new StringBuilder(stripped.Length);
        foreach (var c in stripped)
        {
            switch (c)
            {
                case '\n':
                case '\r':
                    sb.Append(' ');
                    break;
                case '\t':
                    sb.Append(' ');
                    break;
                default:
                    if (!char.IsControl(c))
                        sb.Append(c);
                    break;
            }
        }

        return sb.ToString();
    }

    /// <summary>Invariant numeric padding (never locale-dependent digits).</summary>
    public static string Num(int value) => value.ToString(CultureInfo.InvariantCulture);

    /// <summary>
    /// Collapse newlines/tabs/control codes but <b>keep</b> ANSI sequences: used by the frame
    /// grid, whose rows are pre-coloured and must not lose their colours when sanitised.
    /// </summary>
    public static string SingleLinePreservingAnsi(string? text)
    {
        if (string.IsNullOrEmpty(text))
            return string.Empty;

        var sb = new StringBuilder(text.Length);
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (c == Esc)
            {
                var end = SkipEscape(text, i);
                sb.Append(text, i, end - i + 1);
                i = end;
                continue;
            }

            if (c is '\n' or '\r' or '\t')
            {
                sb.Append(' ');
                continue;
            }

            if (char.IsControl(c))
                continue;

            sb.Append(c);
        }

        return sb.ToString();
    }
}

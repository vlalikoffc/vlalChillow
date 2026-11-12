using System.Text;

namespace StandChillow.LanServer.Dashboard;

/// <summary>
/// A full-screen frame: a fixed grid of rows that is painted with absolute cursor addressing.
/// </summary>
/// <remarks>
/// <b>This is the actual fix for "летящие строки".</b> The old renderer emitted
/// <c>ESC[H</c> followed by rows separated by <c>'\n'</c>. Two things went wrong there:
/// <list type="bullet">
/// <item>When a frame had more rows than the window, the extra rows made the terminal scroll,
/// so every "<c>ESC[H</c> + repaint" landed one or more lines lower — the screen crawled
/// upwards, forever.</item>
/// <item>A single over-wide line (chat message, long nickname) wrapped into an extra visual
/// row, which has the same scrolling effect and also desynchronised the row counter.</item>
/// </list>
/// A frame now has exactly <see cref="Height"/> rows, each strictly clipped to
/// <see cref="Width"/> cells, and each row is positioned with <c>ESC[row;1H</c>. Nothing ever
/// scrolls, so even a wrong height cannot desynchronise the layout.
/// </remarks>
internal sealed class DashboardFrame
{
    private readonly string[] _rows;

    public DashboardFrame(int width, int height)
    {
        Width = Math.Max(MinWidth, width);
        Height = Math.Max(MinHeight, height);
        _rows = new string[Height];
        // Start as *blank but full-width* rows: every row must always be exactly Width cells,
        // otherwise the renderer's contract (and the self-check) would be violated, and a row
        // left over from the previous frame would not be cleared.
        Array.Fill(_rows, new string(' ', Width));
    }

    public const int MinWidth = 60;
    public const int MinHeight = 16;

    public int Width { get; }

    public int Height { get; }

    /// <summary>Rows currently filled (for a cheap "did anything change" check).</summary>
    public int Filled { get; private set; }

    /// <summary>Write one row; index is clamped into the frame, so a layout bug cannot scroll.</summary>
    public void Set(int row, string? content)
    {
        if (row < 0 || row >= Height)
            return;
        _rows[row] = Layout.Fit(Layout.SingleLinePreservingAnsi(content), Width);
    }

    /// <summary>Write one row with a background/band applied end-to-end.</summary>
    public void SetBanded(int row, string? content, string background) =>
        Set(row, background + Layout.Fit(content, Width) + Ansi.Reset);

    /// <summary>Append a block of rows starting at <paramref name="startRow"/> (bottom-clipped).</summary>
    public int AddRange(int startRow, IEnumerable<string?> rows)
    {
        var row = startRow;
        foreach (var r in rows)
        {
            if (row >= Height)
                break;
            Set(row, r);
            row++;
        }

        return row;
    }

    /// <summary>Blank rows from <paramref name="startRow"/> to the bottom (clears stale content).</summary>
    public void ClearRows(int startRow)
    {
        var blank = new string(' ', Width);
        for (var r = Math.Max(0, startRow); r < Height; r++)
            _rows[r] = blank;
    }

    /// <summary>Cursor position for the input caret (1-based row + column, clamped).</summary>
    public static string CursorTo(int row, int column) =>
        Ansi.MoveTo(Math.Max(1, row), Math.Max(1, column));

    /// <summary>Serialize every row for painting (absolute positions, no newlines).</summary>
    public string Paint(int cursorRow = 0, int cursorColumn = 0, bool showCursorAt = false)
    {
        var sb = new StringBuilder(Width * Height + Height * 12 + 64);
        sb.Append(Ansi.HideCursor);
        for (var r = 0; r < Height; r++)
        {
            sb.Append(Ansi.MoveTo(r + 1, 1));
            sb.Append(_rows[r]);
            sb.Append(Ansi.ClearToLineEnd);
        }

        // Park the caret on the input line so typing is visible; the cursor itself stays hidden
        // (we draw our own) unless the caller explicitly wants it.
        if (showCursorAt)
            sb.Append(CursorTo(cursorRow, cursorColumn)).Append(Ansi.ShowCursor);
        else
            sb.Append(CursorTo(cursorRow, cursorColumn)).Append(Ansi.HideCursor);

        return sb.ToString();
    }

    /// <summary>Serialization used by tests: rows only, no escapes.</summary>
    public IReadOnlyList<string> ToPlainRows()
    {
        var list = new List<string>(Height);
        foreach (var row in _rows)
            list.Add(Ansi.StripAnsi(row));
        return list;
    }

    /// <summary>Raw rows (with colours) — used by the renderer to compare frames.</summary>
    public IReadOnlyList<string> Rows => _rows;

    /// <summary>True when the two frames would paint identical pixels.</summary>
    public bool SameAs(DashboardFrame other)
    {
        if (other.Width != Width || other.Height != Height)
            return false;
        for (var i = 0; i < Height; i++)
            if (!string.Equals(_rows[i], other._rows[i], StringComparison.Ordinal))
                return false;
        return true;
    }
}

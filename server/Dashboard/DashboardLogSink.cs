using System.Text;

namespace StandChillow.LanServer.Dashboard;

/// <summary>
/// Bounded ring buffer of recent log lines, shown in the dashboard's "ЛОГ" panel.
/// </summary>
/// <remarks>
/// In dashboard mode <see cref="RunLog"/> redirects <see cref="Console.Out"/> to files and the
/// terminal shows only the dashboard — which meant the operator could not see <c>[health]</c>,
/// <c>[match-host]</c> or <c>[mode]</c> lines at all without a second shell. This sink is
/// installed as <see cref="RunLog.Tap"/> (called on the log writer thread, never on the network
/// thread) and keeps the last <see cref="Capacity"/> sanitised lines for display.
/// </remarks>
public static class DashboardLogSink
{
    private const int Capacity = 400;
    private const int MaxLineCells = 400;

    private static readonly object Gate = new();
    private static readonly Queue<Entry> Lines = new(Capacity);
    private static long _sequence;

    /// <summary>One captured log line.</summary>
    public readonly record struct Entry(long Seq, string Text, DateTime WhenUtc);

    /// <summary>True when the sink has been installed (dashboard mode).</summary>
    public static bool Installed { get; private set; }

    /// <summary>Install as the <see cref="RunLog"/> tap. Idempotent.</summary>
    public static void Install()
    {
        if (Installed)
            return;
        Installed = true;
        RunLog.Tap = Ingest;
    }

    /// <summary>Detach and drop buffered lines.</summary>
    public static void Uninstall()
    {
        Installed = false;
        RunLog.Tap = null;
        lock (Gate)
        {
            Lines.Clear();
        }
    }

    /// <summary>Remove every buffered line (Ctrl+L / dashboard-local clear).</summary>
    public static void Clear()
    {
        lock (Gate)
        {
            Lines.Clear();
        }
    }

    /// <summary>Last <paramref name="count"/> lines, oldest first.</summary>
    public static IReadOnlyList<Entry> Tail(int count)
    {
        lock (Gate)
        {
            if (count <= 0 || Lines.Count == 0)
                return Array.Empty<Entry>();
            if (count >= Lines.Count)
                return Lines.ToArray();

            var skip = Lines.Count - count;
            var list = new List<Entry>(count);
            var i = 0;
            foreach (var e in Lines)
            {
                if (i++ < skip)
                    continue;
                list.Add(e);
            }

            return list;
        }
    }

    /// <summary>Total buffered lines.</summary>
    public static int Count
    {
        get
        {
            lock (Gate)
            {
                return Lines.Count;
            }
        }
    }

    /// <summary>Take a batch of already-formatted log text (may contain many lines).</summary>
    internal static void Ingest(string chunk)
    {
        if (string.IsNullOrEmpty(chunk))
            return;

        var now = DateTime.UtcNow;
        var start = 0;
        for (var i = 0; i < chunk.Length; i++)
        {
            if (chunk[i] != '\n')
                continue;
            Push(chunk.AsSpan(start, i - start), now);
            start = i + 1;
        }

        if (start < chunk.Length)
            Push(chunk.AsSpan(start), now);
    }

    private static void Push(ReadOnlySpan<char> raw, DateTime now)
    {
        var text = Layout.SingleLine(raw.ToString().TrimEnd('\r'));
        if (text.Length == 0)
            return;
        if (text.Length > MaxLineCells * 4)
            text = text[..(MaxLineCells * 4)];

        lock (Gate)
        {
            Lines.Enqueue(new Entry(++_sequence, text, now));
            while (Lines.Count > Capacity)
                Lines.Dequeue();
        }
    }

    /// <summary>Colour key for one line, based on its bracketed prefix.</summary>
    public static (string Color, bool Bold) StyleFor(string line)
    {
        if (line.StartsWith("[health]", StringComparison.Ordinal))
            return (Ansi.Muted, false);
        if (line.StartsWith("[match-host]", StringComparison.Ordinal))
            return (Ansi.Ct, false);
        if (line.StartsWith("[lobby]", StringComparison.Ordinal))
            return (Ansi.Tr, false);
        if (line.StartsWith("[mode", StringComparison.Ordinal))
            return (Ansi.Accent, false);
        if (line.StartsWith("[config]", StringComparison.Ordinal))
            return (Ansi.Neutral, false);
        if (line.StartsWith("[net]", StringComparison.Ordinal)
            || line.StartsWith("[lan]", StringComparison.Ordinal)
            || line.StartsWith("[discovery]", StringComparison.Ordinal))
            return (Ansi.Faint, false);

        var lower = line.ToLowerInvariant();
        if (lower.Contains("error") || lower.Contains("fatal") || lower.Contains("failed")
            || lower.Contains("ошибк"))
            return (Ansi.Danger, true);
        if (lower.Contains("warn") || lower.Contains("skip"))
            return (Ansi.Tr, false);

        return (Ansi.Muted, false);
    }

    /// <summary>
    /// Render one log line as a fixed-width, colour-coded cell. The raw text is sanitised
    /// (no escapes, no newlines) so a log message can never repaint the screen.
    /// </summary>
    public static string Format(Entry entry, int cells)
    {
        if (cells <= 0)
            return string.Empty;

        var (color, bold) = StyleFor(entry.Text);
        var ts = entry.WhenUtc.ToLocalTime().ToString("HH:mm:ss");

        // "HH:mm:ss " + text, dim timestamp.
        var stamp = $"{Ansi.Faint}{ts}{Ansi.Reset} ";
        var room = Math.Max(0, cells - Layout.CellWidth(stamp));
        var body = color + (bold ? Ansi.Bold : string.Empty) + Layout.ClipPlain(entry.Text, room) + Ansi.Reset;
        return stamp + Layout.Fit(body, room);
    }

    /// <summary>Plain text of the buffer (tests / crash dumps).</summary>
    public static string Dump()
    {
        var sb = new StringBuilder();
        foreach (var e in Tail(Capacity))
            sb.Append(e.Text).Append('\n');
        return sb.ToString();
    }
}

using System.Text;

namespace StandChillow.LanServer;

/// <summary>
/// Routes <see cref="Console.Out"/> to log files. Two modes:
/// <list type="bullet">
/// <item>Tee mode (default / ConnectAsClient): Console still prints to the terminal AND is
/// mirrored to the log files, keeping the old behaviour for the learning client.</item>
/// <item>Dashboard mode (dedicated host): Console.WriteLine goes to the log files ONLY so the
/// terminal is free for <see cref="Dashboard.CliDashboard"/>. The real terminal writer is kept
/// as <see cref="TerminalOut"/> for the dashboard to draw into.</item>
/// </list>
/// Always writes a stable <c>server/latest.log</c> (truncated on start) plus a timestamped
/// <c>logs/run-*.log</c> under the binary dir.
/// </summary>
public static class RunLog
{
    private static TextWriter? _installed;

    /// <summary>Stable human log path (<c>server/latest.log</c>, or next to the binary).</summary>
    public static string? Path { get; private set; }

    /// <summary>Timestamped rolling log under the binary <c>logs/</c> dir.</summary>
    public static string? RollingPath { get; private set; }

    /// <summary>The real terminal writer captured before redirection — used by the dashboard.</summary>
    public static TextWriter TerminalOut { get; private set; } = Console.Out;

    /// <summary>True when Console output is file-only (dashboard owns the terminal).</summary>
    public static bool DashboardMode { get; private set; }

    /// <summary>
    /// Install log writers. When <paramref name="dashboardMode"/> is true the terminal is left
    /// alone (Console.WriteLine no longer reaches it) so the CLI dashboard can own the screen.
    /// </summary>
    public static string Start(bool dashboardMode = false)
    {
        if (Path is not null)
            return Path;

        TerminalOut = Console.Out;
        DashboardMode = dashboardMode;

        // logs/run-*.log next to the binary (kept — useful for deep protocol debugging).
        var rollingDir = System.IO.Path.Combine(AppContext.BaseDirectory, "logs");
        Directory.CreateDirectory(rollingDir);
        RollingPath = System.IO.Path.Combine(rollingDir, $"run-{DateTime.Now:yyyyMMdd_HHmmss}.log");
        var rolling = OpenWriter(RollingPath, append: false);

        // server/latest.log — stable path, truncated (rotated) on each start.
        var latestPath = System.IO.Path.Combine(ResolveServerDir(), "latest.log");
        var latest = OpenWriter(latestPath, append: false);
        Path = latestPath;

        var writers = new List<TextWriter> { rolling, latest };
        if (!dashboardMode)
            writers.Insert(0, TerminalOut);

        _installed = new MultiWriter(writers.ToArray(), TerminalOut.Encoding);
        Console.SetOut(_installed);
        return Path;
    }

    private static StreamWriter OpenWriter(string path, bool append)
    {
        var stream = new FileStream(
            path,
            append ? FileMode.Append : FileMode.Create,
            FileAccess.Write,
            FileShare.ReadWrite);
        return new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false))
        {
            AutoFlush = true,
        };
    }

    /// <summary>
    /// Resolve the repo <c>server/</c> dir: walk up from the binary until the csproj is found,
    /// else fall back to the current working directory (which is <c>server/</c> under
    /// <c>dotnet run</c>).
    /// </summary>
    private static string ResolveServerDir()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(System.IO.Path.Combine(dir.FullName, "StandChillow.LanServer.csproj")))
                return dir.FullName;
            dir = dir.Parent;
        }
        return Directory.GetCurrentDirectory();
    }

    /// <summary>Fan-out <see cref="TextWriter"/> that writes each call to every sink under a lock.</summary>
    private sealed class MultiWriter : TextWriter
    {
        private readonly TextWriter[] _sinks;
        private readonly object _gate = new();
        private readonly Encoding _encoding;

        public MultiWriter(TextWriter[] sinks, Encoding encoding)
        {
            _sinks = sinks;
            _encoding = encoding;
        }

        public override Encoding Encoding => _encoding;

        public override void Write(char value)
        {
            lock (_gate)
                foreach (var s in _sinks) s.Write(value);
        }

        public override void Write(string? value)
        {
            lock (_gate)
                foreach (var s in _sinks) s.Write(value);
        }

        public override void Write(char[] buffer, int index, int count)
        {
            lock (_gate)
                foreach (var s in _sinks) s.Write(buffer, index, count);
        }

        public override void WriteLine(string? value)
        {
            lock (_gate)
                foreach (var s in _sinks) s.WriteLine(value);
        }

        public override void Flush()
        {
            lock (_gate)
                foreach (var s in _sinks) s.Flush();
        }
    }
}

using System.Collections.Concurrent;
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
/// <remarks>
/// <b>Stability:</b> file writes happen on a single background writer thread, not on the
/// caller (which is usually the LiteNetLib poll loop). Console/log writes are enqueued and
/// bounded: when the queue is full the oldest line is dropped instead of blocking the
/// network thread or growing without bound. Previously every <c>Console.WriteLine</c>
/// flushed synchronously to two files under a lock — with 10 players that alone was enough
/// to stall packet processing.
/// </remarks>
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

    /// <summary>Lines dropped because the background writer could not keep up.</summary>
    public static long LinesDropped => Interlocked.Read(ref _dropped);

    /// <summary>
    /// Optional observer for formatted log batches (used by the dashboard log panel).
    /// Invoked on the single log-writer thread, so implementations must be cheap and
    /// thread-safe. Must never throw; exceptions are swallowed by the writer.
    /// </summary>
    public static Action<string>? Tap { get; set; }

    private static long _dropped;
    private static long _written;
    private static long _bytes;
    private static MultiWriter? _multi;

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

        // server/latest.log — stable path, truncated (rotated) on each start.
        var latestPath = System.IO.Path.Combine(ResolveServerDir(), "latest.log");
        Path = latestPath;

        var writers = new List<TextWriter>();
        if (!dashboardMode)
            writers.Add(TerminalOut);

        _multi = new MultiWriter(writers.ToArray(), latestPath, RollingPath, TerminalOut.Encoding);
        _installed = _multi;
        Console.SetOut(_installed);
        return Path;
    }

    /// <summary>
    /// Flush everything still queued and stop the background writer (called on graceful exit).
    /// </summary>
    public static void Shutdown(TimeSpan? timeout = null)
    {
        try { _multi?.Complete(timeout ?? TimeSpan.FromSeconds(2)); }
        catch { /* best effort at shutdown */ }
    }

    /// <summary>One-line counters for the periodic heartbeat.</summary>
    public static string DescribeStats() =>
        $"logWritten={Interlocked.Read(ref _written)} logDropped={Interlocked.Read(ref _dropped)} " +
        $"logBytes={Interlocked.Read(ref _bytes)}";

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

    /// <summary>
    /// Bounded fan-out writer: appends to a queue drained by a single background thread.
    /// Writes never block the caller and never throw.
    /// </summary>
    private sealed class MultiWriter : TextWriter, IDisposable
    {
        private const int Capacity = 32_768;

        private readonly TextWriter? _terminal;
        private readonly string _latestPath;
        private readonly string _rollingPath;
        private readonly Encoding _encoding;
        private readonly BlockingCollection<string> _queue =
            new(new ConcurrentQueue<string>(), Capacity);
        private readonly Thread _thread;

        public MultiWriter(
            TextWriter[] sinks,
            string latestPath,
            string rollingPath,
            Encoding encoding)
        {
            _terminal = sinks.Length > 0 ? sinks[0] : null;
            _latestPath = latestPath;
            _rollingPath = rollingPath;
            _encoding = encoding;

            _thread = new Thread(WritersLoop)
            {
                IsBackground = true,
                Name = "runlog-writer",
                Priority = ThreadPriority.BelowNormal,
            };
            _thread.Start();
        }

        public override Encoding Encoding => _encoding;

        public override void Write(char value) => Enqueue(value.ToString());

        public override void Write(string? value)
        {
            if (!string.IsNullOrEmpty(value))
                Enqueue(value);
        }

        public override void Write(char[] buffer, int index, int count) =>
            Enqueue(new string(buffer, index, count));

        public override void WriteLine(string? value) => Enqueue((value ?? string.Empty) + "\n");

        public override void Flush()
        {
            // No-op: the background writer flushes on its own schedule, and callers
            // must never pay for disk latency.
        }

        private void Enqueue(string text)
        {
            // Never block a network thread on logging: drop the line if the queue is full.
            if (!_queue.TryAdd(text))
                Interlocked.Increment(ref _dropped);
        }

        private void WritersLoop()
        {
            StreamWriter? latest = null;
            StreamWriter? rolling = null;
            var lastFlush = Environment.TickCount64;
            var pending = new StringBuilder();

            try
            {
                latest = Open(_latestPath);
                rolling = Open(_rollingPath);

                foreach (var chunk in _queue.GetConsumingEnumerable())
                {
                    pending.Append(chunk);
                    if (!_queue.IsCompleted && _queue.Count > 0 && pending.Length < 64 * 1024)
                        continue;

                    WriteBatch(latest, rolling, pending);
                    pending.Clear();

                    var now = Environment.TickCount64;
                    if (now - lastFlush >= 1000)
                    {
                        lastFlush = now;
                        try { latest.Flush(); } catch { /* ignore */ }
                        try { rolling?.Flush(); } catch { /* ignore */ }
                    }
                }

                if (pending.Length > 0)
                    WriteBatch(latest, rolling, pending);
            }
            catch (Exception ex)
            {
                try { TerminalOut.WriteLine($"[log] writer stopped: {ex.Message}"); }
                catch { /* ignore */ }
            }
            finally
            {
                try { latest?.Flush(); } catch { /* ignore */ }
                try { rolling?.Flush(); } catch { /* ignore */ }
                try { latest?.Dispose(); } catch { /* ignore */ }
                try { rolling?.Dispose(); } catch { /* ignore */ }
            }
        }

        private void WriteBatch(StreamWriter? latest, StreamWriter? rolling, StringBuilder batch)
        {
            if (batch.Length == 0)
                return;

            var text = batch.ToString();
            try { latest?.Write(text); } catch { /* ignore */ }
            try { rolling?.Write(text); } catch { /* ignore */ }
            if (_terminal is not null)
            {
                try { _terminal.Write(text); _terminal.Flush(); } catch { /* ignore */ }
            }

            // Non-blocking observer (dashboard log panel). Runs on this writer thread only —
            // never on a network thread — and must not throw.
            var tap = Tap;
            if (tap is not null)
            {
                try { tap(text); } catch { /* display-only */ }
            }

            Interlocked.Increment(ref _written);
            Interlocked.Add(ref _bytes, text.Length);
        }

        private static StreamWriter Open(string path)
        {
            var stream = new FileStream(
                path,
                FileMode.Create,
                FileAccess.Write,
                FileShare.ReadWrite,
                bufferSize: 64 * 1024,
                useAsync: false);
            // AutoFlush off: batching + periodic flush keeps the disk quiet.
            return new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false))
            {
                AutoFlush = false,
            };
        }

        /// <summary>Stop accepting lines, drain the queue, and join the writer thread.</summary>
        public void Complete(TimeSpan timeout)
        {
            try { _queue.CompleteAdding(); } catch { /* ignore */ }
            try { _thread.Join(timeout); } catch { /* ignore */ }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
                Complete(TimeSpan.FromSeconds(2));
            base.Dispose(disposing);
        }
    }
}

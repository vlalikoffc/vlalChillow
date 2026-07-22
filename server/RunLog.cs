using System.Text;

namespace StandChillow.LanServer;

/// <summary>
/// Mirrors <see cref="Console.Out"/> to a run log file so long sessions keep early join/Found lines.
/// </summary>
public static class RunLog
{
    private static DualWriter? _dual;

    public static string? Path { get; private set; }

    /// <summary>Create logs/run-*.log under the binary dir and tee Console.Out into it.</summary>
    public static string Start()
    {
        if (Path is not null)
            return Path;

        var dir = System.IO.Path.Combine(AppContext.BaseDirectory, "logs");
        Directory.CreateDirectory(dir);
        var file = System.IO.Path.Combine(dir, $"run-{DateTime.Now:yyyyMMdd_HHmmss}.log");
        var stream = new FileStream(file, FileMode.Create, FileAccess.Write, FileShare.ReadWrite);
        var fileWriter = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false))
        {
            AutoFlush = true,
        };
        _dual = new DualWriter(Console.Out, fileWriter);
        Console.SetOut(_dual);
        Path = file;
        return file;
    }

    private sealed class DualWriter : TextWriter
    {
        private readonly TextWriter _a;
        private readonly TextWriter _b;
        private readonly object _gate = new();

        public DualWriter(TextWriter a, TextWriter b)
        {
            _a = a;
            _b = b;
        }

        public override Encoding Encoding => _a.Encoding;

        public override void Write(char value)
        {
            lock (_gate)
            {
                _a.Write(value);
                _b.Write(value);
            }
        }

        public override void Write(string? value)
        {
            lock (_gate)
            {
                _a.Write(value);
                _b.Write(value);
            }
        }

        public override void Write(char[] buffer, int index, int count)
        {
            lock (_gate)
            {
                _a.Write(buffer, index, count);
                _b.Write(buffer, index, count);
            }
        }

        public override void WriteLine(string? value)
        {
            lock (_gate)
            {
                _a.WriteLine(value);
                _b.WriteLine(value);
            }
        }

        public override void Flush()
        {
            lock (_gate)
            {
                _a.Flush();
                _b.Flush();
            }
        }
    }
}

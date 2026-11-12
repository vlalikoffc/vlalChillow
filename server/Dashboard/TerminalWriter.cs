using System.Text;

namespace StandChillow.LanServer.Dashboard;

/// <summary>
/// UTF-8 writer over the real stdout handle, independent of <see cref="Console.Out"/>.
/// </summary>
/// <remarks>
/// <see cref="Console.OutputEncoding"/> replaces <c>Console.Out</c> with a new writer; anything
/// that captured the old one (as the dashboard did) keeps encoding with the old code page, so
/// Russian text turned into <c>?</c> or mojibake. Writing through our own
/// <see cref="StreamWriter"/> with a fixed UTF-8 encoder removes that race: whatever the host
/// does to <c>Console.Out</c>, the dashboard always puts UTF-8 bytes on the wire.
/// </remarks>
internal static class TerminalWriter
{
    private static TextWriter? _writer;

    /// <summary>UTF-8 writer for the dashboard. Never throws; falls back to <see cref="Console.Out"/>.</summary>
    public static TextWriter Instance
    {
        get
        {
            if (_writer is not null)
                return _writer;

            try
            {
                var stdout = Console.OpenStandardOutput();
                _writer = new StreamWriter(stdout, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), 8192)
                {
                    AutoFlush = false,
                };
            }
            catch
            {
                _writer = Console.Out;
            }

            return _writer;
        }
    }

    /// <summary>Flush any buffered frame bytes to the terminal.</summary>
    public static void Flush()
    {
        try { Instance.Flush(); } catch { /* terminal gone — nothing to do */ }
    }
}

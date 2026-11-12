using System.Runtime.InteropServices;
using System.Text;

namespace StandChillow.LanServer.Dashboard;

/// <summary>
/// Decides whether the full-screen dashboard can be drawn, and puts the console into a state
/// where it can (UTF-8 + VT processing).
/// </summary>
/// <remarks>
/// Three separate things used to be conflated, and each one broke the dashboard on its own:
/// <list type="number">
/// <item><b>Encoding.</b> Russian chat/nicknames arrived as <c>????</c> or mojibake unless
/// <c>Console.OutputEncoding</c> was UTF-8 <i>and</i> the input side matched. Setting it late
/// (inside <c>Start()</c>, after the host had already printed its banner) left the first frames
/// broken, and <see cref="Console.InputEncoding"/> was never set, so typed Cyrillic commands
/// came back as garbage.</item>
/// <item><b>VT support.</b> Legacy <c>conhost</c> (plain <c>cmd.exe</c>, PowerShell 5) ignores
/// ANSI unless <c>ENABLE_VIRTUAL_TERMINAL_PROCESSING</c> is on; when it is off the screen fills
/// with literal <c>←[96m</c> instead of a dashboard.</item>
/// <item><b>Redirection.</b> Piping stdout to a file/log must never receive escape codes.</item>
/// </list>
/// </remarks>
public static class TerminalCapabilities
{
    /// <summary>Box/symbol glyphs safe for the current console.</summary>
    public readonly record struct Glyphs(
        string TopLeft,
        string TopRight,
        string BottomLeft,
        string BottomRight,
        string Horizontal,
        string Vertical)
    {
        /// <summary>Unicode box drawing (one cell wide in every common terminal font).</summary>
        public static readonly Glyphs Unicode = new("┌", "┐", "└", "┘", "─", "│");

        /// <summary>ASCII fallback for conhost fonts without box drawing.</summary>
        public static readonly Glyphs Ascii = new("+", "+", "+", "+", "-", "|");

        public string Top(int cells) => TopLeft + Layout.Bar(cells, Horizontal[0]) + TopRight;

        public string Bottom(int cells) => BottomLeft + Layout.Bar(cells, Horizontal[0]) + BottomRight;
    }

    private static bool _probed;
    private static bool _vt;
    private static bool _unicode = true;
    private static bool _encodingConfigured;
    private static Glyphs _glyphs = Glyphs.Unicode;

    /// <summary>Box glyphs to use (Unicode on capable consoles, ASCII otherwise).</summary>
    public static Glyphs CurrentGlyphs
    {
        get
        {
            Probe();
            return _glyphs;
        }
    }

    /// <summary>True when ANSI/VT sequences are understood by the current terminal.</summary>
    public static bool AnsiSupported
    {
        get
        {
            Probe();
            return _vt;
        }
    }

    /// <summary>
    /// Set UTF-8 on stdout and stdin. Idempotent, never throws; returns true when the console
    /// is known to be UTF-8 (false means "we tried, the terminal decides").
    /// </summary>
    public static bool ConfigureConsoleEncoding()
    {
        if (_encodingConfigured)
            return true;
        _encodingConfigured = true;

        var ok = true;
        try
        {
            if (Console.OutputEncoding.CodePage != Encoding.UTF8.CodePage)
                Console.OutputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
        }
        catch
        {
            ok = false;
        }

        try
        {
            if (!OperatingSystem.IsWindows())
                Console.InputEncoding = Encoding.UTF8;
            else if (Console.InputEncoding.CodePage != Encoding.UTF8.CodePage)
                Console.InputEncoding = Encoding.UTF8;
        }
        catch
        {
            // Redirected stdin / no console — harmless.
        }

        // Keep console handles in UTF-8 on Windows even when Console.* bookkeeping lies.
        if (OperatingSystem.IsWindows())
        {
            try
            {
                SetConsoleOutputCP(65001);
                SetConsoleCP(65001);
            }
            catch (DllNotFoundException) { }
            catch (EntryPointNotFoundException) { }
        }

        return ok;
    }

    /// <summary>
    /// True when the dashboard should own the screen. Requires an interactive terminal with
    /// working VT support; everything else falls back to plain logging.
    /// </summary>
    public static bool CanUseDashboard()
    {
        if (Console.IsOutputRedirected || Console.IsInputRedirected)
            return false;

        var term = Environment.GetEnvironmentVariable("TERM");
        if (string.Equals(term, "dumb", StringComparison.OrdinalIgnoreCase))
            return false;
        if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("NO_COLOR")))
            return false;

        ConfigureConsoleEncoding();
        Probe();
        return _vt;
    }

    /// <summary>Force-enables the plain (no ANSI) dashboard, mainly for tests and conhost.</summary>
    public static void ForcePlainGlyphs()
    {
        _probed = true;
        _unicode = false;
        _glyphs = Glyphs.Ascii;
    }

    private static void Probe()
    {
        if (_probed)
            return;
        _probed = true;

        try
        {
            if (!OperatingSystem.IsWindows())
            {
                _vt = true; // Linux / macOS terminals understand ANSI by default.
                _unicode = !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("LANG"))
                           || !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("LC_ALL"))
                           || !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("LC_CTYPE"));

                // A bare C/POSIX locale has no UTF-8 — box drawing would be mojibake.
                var builtIn = Environment.GetEnvironmentVariable("WT_SESSION") is not null
                              || Environment.GetEnvironmentVariable("TERM_PROGRAM") is { Length: > 0 };
                _glyphs = _unicode || builtIn ? Glyphs.Unicode : Glyphs.Ascii;
                return;
            }

            _vt = TryEnableWindowsVirtualTerminal();
            _unicode = CodePageSupportsUnicode();
            _glyphs = _unicode ? Glyphs.Unicode : Glyphs.Ascii;
        }
        catch
        {
            _vt = false;
            _unicode = false;
            _glyphs = Glyphs.Ascii;
        }
    }

    private static bool CodePageSupportsUnicode()
    {
        try
        {
            return GetConsoleOutputCP() == 65001;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Enable <c>ENABLE_VIRTUAL_TERMINAL_PROCESSING</c> (+ processed output) on the stdout
    /// handle. Returns false on pre-Windows-10 conhost, where the caller must use plain output.
    /// </summary>
    public static bool TryEnableWindowsVirtualTerminal()
    {
        if (!OperatingSystem.IsWindows())
            return true;

        try
        {
            var handle = GetStdHandle(StdOutputHandle);
            if (handle == InvalidHandleValue || !GetConsoleMode(handle, out var mode))
                return false;

            const uint enableProcessedOutput = 0x0001;
            const uint enableVirtualTerminalProcessing = 0x0004;
            var required = enableProcessedOutput | enableVirtualTerminalProcessing;
            if ((mode & required) == required)
                return true;

            return SetConsoleMode(handle, mode | required);
        }
        catch (DllNotFoundException) { return false; }
        catch (EntryPointNotFoundException) { return false; }
    }

    /// <summary>Hide the hardware cursor when supported (ANSI hide is used otherwise).</summary>
    public static void SetCursorVisible(bool visible)
    {
        try
        {
            if (!Console.IsOutputRedirected)
                Console.CursorVisible = visible;
        }
        catch
        {
            // Not every terminal supports it — the ANSI sequence is the fallback.
        }
    }

    private const int StdOutputHandle = -11;
    private static readonly IntPtr InvalidHandleValue = new(-1);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GetStdHandle(int nStdHandle);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetConsoleMode(IntPtr hConsoleHandle, out uint lpMode);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetConsoleMode(IntPtr hConsoleHandle, uint dwMode);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint GetConsoleOutputCP();

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetConsoleOutputCP(uint wCodePageID);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetConsoleCP(uint wCodePageID);
}

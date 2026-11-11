using System.Runtime.InteropServices;

namespace StandChillow.LanServer.Dashboard;

/// <summary>Checks that a real terminal can render the dashboard instead of raw ANSI/log lines.</summary>
public static class TerminalCapabilities
{
    public static bool CanUseDashboard()
    {
        if (Console.IsOutputRedirected || Console.IsInputRedirected)
            return false;
        if (string.Equals(Environment.GetEnvironmentVariable("TERM"), "dumb", StringComparison.OrdinalIgnoreCase))
            return false;
        if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("NO_COLOR")))
            return false;

        // Modern Windows Terminal works immediately. Legacy conhost needs VT processing enabled;
        // without this the dashboard appears as lines containing literal ESC[... sequences.
        return !OperatingSystem.IsWindows() || TryEnableWindowsVirtualTerminal();
    }

    private static bool TryEnableWindowsVirtualTerminal()
    {
        try
        {
            var handle = GetStdHandle(StdOutputHandle);
            if (handle == InvalidHandleValue || !GetConsoleMode(handle, out var mode))
                return false;
            const uint enableProcessedOutput = 0x0001;
            const uint enableVirtualTerminalProcessing = 0x0004;
            var required = enableProcessedOutput | enableVirtualTerminalProcessing;
            return (mode & required) == required || SetConsoleMode(handle, mode | required);
        }
        catch (DllNotFoundException) { return false; }
        catch (EntryPointNotFoundException) { return false; }
    }

    private const int StdOutputHandle = -11;
    private static readonly IntPtr InvalidHandleValue = new(-1);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GetStdHandle(int nStdHandle);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetConsoleMode(IntPtr hConsoleHandle, out uint lpMode);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetConsoleMode(IntPtr hConsoleHandle, uint dwMode);
}

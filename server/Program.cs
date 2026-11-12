// влалChillow — LAN dedicated server for StandChillow
// Copyright (C) 2026 влал (vlalikoffc)
//
// This program is free software: you can redistribute it and/or modify
// it under the terms of the GNU General Public License as published by
// the Free Software Foundation, either version 3 of the License, or
// (at your option) any later version.
//
// This program is distributed in the hope that it will be useful,
// but WITHOUT ANY WARRANTY; without even the implied warranty of
// MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
// GNU General Public License for more details.
//
// You should have received a copy of the GNU General Public License
// along with this program. If not, see <https://www.gnu.org/licenses/>.

using System.Text;
using StandChillow.LanServer;
using StandChillow.LanServer.Config;
using StandChillow.LanServer.Dashboard;
using StandChillow.LanServer.Diagnostics;
using StandChillow.LanServer.Net.Match;
using StandChillow.LanServer.Startup;

// CLI:
//   Dedicated host (default): dotnet run -c Release [--debug-chat] [-- lobbyTitle]
//   ConnectAsClient learning: dotnet run -c Release -- --client [--team ct|tr] [options]
// Captures are evidence only — never replay them into dedicated-host replies.

// UTF-8 до первого вывода: иначе русский текст в консоли превращается в '?' или кракозябры,
// а на Windows ещё и VT-последовательности дашборда печатаются как текст.
TerminalCapabilities.ConfigureConsoleEncoding();

// --dashboard-check: отрисовать раскладку без терминала (CI / ssh без TTY) и выйти.
// --dashboard-dump:  напечатать кадры обычным текстом (без ANSI) для глазами-проверки.
if (args.Contains("--dashboard-check", StringComparer.Ordinal))
{
    Environment.ExitCode = DashboardSelfCheck.Run(Console.Out);
    return;
}

if (args.Contains("--dashboard-dump", StringComparer.Ordinal))
{
    Console.OutputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
    DashboardSelfCheck.Dump(Console.Out);
    return;
}

// Проба (--client) идёт первой веткой и конфиг НЕ читает вообще:
// ни debug, ни captures, ни timings — поведение пробы от конфига не зависит.
if (IsClientMode(args, out var clientArgs))
{
    var clientLog = RunLog.Start(dashboardMode: false);
    Console.WriteLine($"[log] writing to {clientLog}");
    Console.WriteLine($"[log] read from start: less '{clientLog}'   or   tail -n 200 '{clientLog}'");
    Console.WriteLine();
    await ClientProbeMode.RunAsync(clientArgs);
    return;
}

// Дальше — только выделенный сервер: config.json создаётся/читается здесь.
// load уже напечатал путь, найденные проблемы и применённые вместо них значения,
// поэтому «конфиг не читается» больше не выглядит как тишина.
var serverConfig = ServerConfig.Load();
args = MergeLaunchArgs(args, serverConfig.Launch.Args);

if (IsClientMode(args, out clientArgs))
{
    // Подстраховка: даже если в launch.args что-то просочилось, в пробу не уходим.
    Console.WriteLine("[config] launch.args не может включать режим пробы — игнорирую");
    return;
}

// Never emit full-screen ANSI into redirected/legacy terminals: use readable plain logs there.
// --dashboard forces the UI explicitly; --no-dashboard / --console selects the plain command loop.
var dashboardOverride = ConsumeDashboardFlag(ref args) ?? serverConfig.Launch.Dashboard;
var useDashboard = dashboardOverride ?? TerminalCapabilities.CanUseDashboard();
var logPath = RunLog.Start(dashboardMode: useDashboard);
if (!useDashboard)
{
    Console.WriteLine($"[log] writing to {logPath}");
    Console.WriteLine($"[log] read from start: less '{logPath}'   or   tail -n 200 '{logPath}'");
    Console.WriteLine();
}

ConsumeDebugMatchChatFlag(ref args);
ConsumeVerboseFlag(ref args, serverConfig);
try
{
    await HostBootstrap.RunAsync(args, useDashboard, serverConfig);
}
finally
{
    // Drain queued log lines before the process exits (writer is a background thread).
    RunLog.Shutdown();
}

/// <summary>
/// Доп. аргументы из <c>launch.args</c> дописываются после реальных: консоль главнее.
/// </summary>
static string[] MergeLaunchArgs(string[] args, List<string>? extra)
{
    if (extra is null || extra.Count == 0)
        return args;

    var merged = new List<string>(args);
    foreach (var raw in extra)
    {
        var a = raw?.Trim();
        if (string.IsNullOrEmpty(a))
            continue;
        if (ServerConfig.IsProbeFlag(a))
            continue;
        if (!merged.Contains(a))
            merged.Add(a);
    }

    return merged.ToArray();
}

static bool? ConsumeDashboardFlag(ref string[] args)
{
    bool? useDashboard = null;
    var kept = new List<string>(args.Length);
    foreach (var arg in args)
    {
        if (arg is "--dashboard")
        {
            useDashboard = true;
            continue;
        }
        if (arg is "--no-dashboard" or "--console")
        {
            useDashboard = false;
            continue;
        }
        kept.Add(arg);
    }
    args = kept.ToArray();
    return useDashboard;
}

static bool IsClientMode(string[] args, out string[] rest)
{
    for (var i = 0; i < args.Length; i++)
    {
        var a = args[i];
        if (a is "--client" or "-c" or "connectasclient" or "--connectasclient")
        {
            rest = args.Where((_, idx) => idx != i).ToArray();
            return true;
        }
    }

    rest = args;
    return false;
}

/// <summary>
/// Enables <see cref="MatchHostSettings.DebugMatchChat"/> from <c>--debug-chat</c> /
/// <c>--debug-match-chat</c> or <c>DEBUG_MATCH_CHAT=1|true|yes|on</c>. Strips those flags so
/// they are not mistaken for the lobby title.
/// </summary>
static void ConsumeDebugMatchChatFlag(ref string[] args)
{
    var on = EnvTruthy(Environment.GetEnvironmentVariable("DEBUG_MATCH_CHAT"));
    var explicitFlag = on;
    var kept = new List<string>(args.Length);
    foreach (var a in args)
    {
        if (a is "--debug-chat" or "--debug-match-chat")
        {
            on = true;
            explicitFlag = true;
            continue;
        }

        if (a is "--no-debug-chat")
        {
            on = false;
            explicitFlag = true;
            continue;
        }

        kept.Add(a);
    }

    args = kept.ToArray();
    if (!explicitFlag)
        return; // пусть решает конфиг (debug.matchChat)

    MatchHostSettings.DebugMatchChat = on;
    MatchHostSettings.DebugMatchChatLocked = true;
}

/// <summary>
/// <c>--verbose</c> / <c>VLAL_VERBOSE=1</c>: per-packet protocol traces + packet captures.
/// Off by default because those are exactly the writes that melt a 10-player host.
/// </summary>
static void ConsumeVerboseFlag(ref string[] args, ServerConfig config)
{
    Diag.ApplyEnvironment();
    var cliVerbose = false;
    var kept = new List<string>(args.Length);
    foreach (var a in args)
    {
        if (a is "--verbose" or "-v" or "--debug")
        {
            cliVerbose = true;
            continue;
        }

        kept.Add(a);
    }

    args = kept.ToArray();
    // Отладка: конфиг (debug.*) — база, флаги командной строки и launch.verbose — сверху.
    Diag.CliVerbose |= cliVerbose;
    Diag.CapturesForced = cliVerbose || config.Launch.Verbose || config.Debug.Verbose;
    Diag.Apply(
        config.Debug,
        config.Tuning.HealthIntervalSec,
        config.Launch.Verbose || cliVerbose);
    if (config.Debug.Captures || Diag.CapturesForced)
    {
        // --verbose исторически включает и captures; явный debug.captures остаётся главным.
        CaptureSink.Configure(
            true,
            config.Debug.CapturesDir is { Length: > 0 } dir
                ? dir
                : System.IO.Path.Combine(AppContext.BaseDirectory, "captures"),
            config.Debug.CapturesMaxFiles,
            config.Debug.CapturesMaxBytesMb * 1024L * 1024L);
    }

    Console.WriteLine(Diag.Verbose
        ? "[diag] verbose ON — per-packet logs + captures (expect heavy disk I/O)"
        : "[diag] verbose off — packet captures follow config debug.captures");
}

static bool EnvTruthy(string? value) =>
    !string.IsNullOrWhiteSpace(value)
    && value.Trim() is "1" or "true" or "TRUE" or "True" or "yes" or "YES" or "Yes"
        or "on" or "ON" or "On";

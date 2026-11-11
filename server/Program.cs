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

using StandChillow.LanServer;
using StandChillow.LanServer.Net.Match;
using StandChillow.LanServer.Startup;

// CLI:
//   Dedicated host (default): dotnet run -c Release [--debug-chat] [-- lobbyTitle]
//   ConnectAsClient learning: dotnet run -c Release -- --client [--team ct|tr] [options]
// Captures are evidence only — never replay them into dedicated-host replies.

if (IsClientMode(args, out var clientArgs))
{
    var clientLog = RunLog.Start(dashboardMode: false);
    Console.WriteLine($"[log] writing to {clientLog}");
    Console.WriteLine($"[log] read from start: less '{clientLog}'   or   tail -n 200 '{clientLog}'");
    Console.WriteLine();
    await ClientProbeMode.RunAsync(clientArgs);
    return;
}

// The dashboard owns a real terminal; redirected output keeps ordinary console logging.
var useDashboard = !Console.IsOutputRedirected;
var logPath = RunLog.Start(dashboardMode: useDashboard);
if (!useDashboard)
{
    Console.WriteLine($"[log] writing to {logPath}");
    Console.WriteLine($"[log] read from start: less '{logPath}'   or   tail -n 200 '{logPath}'");
    Console.WriteLine();
}

ConsumeDebugMatchChatFlag(ref args);
await HostBootstrap.RunAsync(args, useDashboard);

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
    var kept = new List<string>(args.Length);
    foreach (var a in args)
    {
        if (a is "--debug-chat" or "--debug-match-chat")
        {
            on = true;
            continue;
        }

        if (a is "--no-debug-chat")
        {
            on = false;
            continue;
        }

        kept.Add(a);
    }

    args = kept.ToArray();
    MatchHostSettings.DebugMatchChat = on;
}

static bool EnvTruthy(string? value) =>
    !string.IsNullOrWhiteSpace(value)
    && value.Trim() is "1" or "true" or "TRUE" or "True" or "yes" or "YES" or "Yes"
        or "on" or "ON" or "On";

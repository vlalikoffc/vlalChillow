using System.Net;
using StandChillow.LanServer.Lan;
using StandChillow.LanServer.Net;
using StandChillow.LanServer.Net.Match;

namespace StandChillow.LanServer.Startup;

/// <summary>Runs the opt-in ConnectAsClient protocol-learning path; never starts a dedicated host.</summary>
public static class ClientProbeMode
{
    // Interactive lobby picker (only used with --pick and a real TTY). Discovery is already
    // quieted by the caller, so the prompt is readable. Enter = index 0; a valid number joins
    // immediately; q/quit cancels; anything else re-prompts (never hangs silently).
    private static int PromptForLobbyIndex(int count)
    {
        while (true)
        {
            Console.Write($"Select index 0..{count - 1} (Enter = 0, q = quit): ");
            var line = Console.ReadLine();
            if (line is null)
            {
                Console.WriteLine("[client] stdin closed — using index 0");
                return 0;
            }
            line = line.Trim();
            if (line.Length == 0)
                return 0;
            if (line is "q" or "quit" or "exit")
                return -1;
            if (int.TryParse(line, out var n) && n >= 0 && n < count)
                return n;
            Console.WriteLine($"[client] '{line}' is not 0..{count - 1} — Enter for 0, q to quit.");
        }
    }

    public static async Task RunAsync(string[] args)
    {
        Console.Title = "влалChillow LAN ConnectAsClient";
        Console.WriteLine("╔══════════════════════════════════════════════════════════╗");
        Console.WriteLine("║  ConnectAsClient — learn from a REAL phone host        ║");
        Console.WriteLine("║  UDP 5056 = discovery probe/reply (Bonjour V2)         ║");
        Console.WriteLine("║  UDP 7778 = LiteNetLib lobby join                      ║");
        Console.WriteLine("║  UDP 7777 = LiteNetLib match after Play (op9)          ║");
        Console.WriteLine("║  Phone must CREATE/host a LAN lobby (search ≠ host)    ║");
        Console.WriteLine("╚══════════════════════════════════════════════════════════╝");
        Console.WriteLine();
        Console.WriteLine("NOTE: Only a hosting phone answers probes on 5056.");
        Console.WriteLine("      On Play, phone sends op9 with game IP:7777 — probe follows that channel.");
        Console.WriteLine("      Match JoinRoom: password=Dedik (UI may say room name), room=--name roster,");
        Console.WriteLine($"      mode=JoinOnly, Handshake AppId={MatchAuth.AppId}.");
        Console.WriteLine("      After Found: stay connected and capture ALL further match RX (world/RPC).");
        Console.WriteLine("      If op9 IP is off our LAN subnet (or connect fails), match uses lobby LAN IP + op9 port.");
        Console.WriteLine("      Lobby pick: default auto-joins first lobby (never hangs); --pick = interactive; --index N = force.");
        Console.WriteLine("      Skip discovery: --ip <phone> --port 7778");
        Console.WriteLine("      Force match IP: --match-ip <lan-ip> (keeps op9 port, usually 7777)");
        Console.WriteLine("      Fighting probe: --team ct|tr  (default Spectator; Ct/Tr spawn at catalog map pose)");
        Console.WriteLine();

        var profileName = "probe";
        var autoPick = false;
        var interactivePick = false;
        string? forceIp = null;
        string? forceMatchIpRaw = null;
        int? forcePort = null;
        int? forceIndex = null;
        var waitSeconds = 12;
        var probeTeam = MatchTeam.Spectator;

        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--auto":
                    autoPick = true;
                    break;
                case "--pick":
                case "--interactive":
                    interactivePick = true;
                    break;
                case "--name" when i + 1 < args.Length:
                    profileName = args[++i];
                    break;
                case "--ip" when i + 1 < args.Length:
                    forceIp = args[++i];
                    break;
                case "--match-ip" when i + 1 < args.Length:
                    forceMatchIpRaw = args[++i];
                    break;
                case "--port" when i + 1 < args.Length && int.TryParse(args[i + 1], out var p):
                    forcePort = p;
                    i++;
                    break;
                case "--index" when i + 1 < args.Length && int.TryParse(args[i + 1], out var idx):
                    forceIndex = idx;
                    i++;
                    break;
                case "--wait" when i + 1 < args.Length && int.TryParse(args[i + 1], out var w):
                    waitSeconds = Math.Clamp(w, 1, 120);
                    i++;
                    break;
                case "--team" when i + 1 < args.Length:
                {
                    var t = args[++i].Trim().ToLowerInvariant();
                    probeTeam = t switch
                    {
                        "ct" or "2" or "counter" => MatchTeam.Ct,
                        "tr" or "t" or "1" or "terror" or "terrorist" => MatchTeam.Tr,
                        "spec" or "spectator" or "3" => MatchTeam.Spectator,
                        _ => MatchTeam.Spectator,
                    };
                    if (t is not ("ct" or "2" or "counter" or "tr" or "t" or "1" or "terror"
                        or "terrorist" or "spec" or "spectator" or "3"))
                        Console.WriteLine($"[client] unknown --team '{t}' — using Spectator");
                    break;
                }
                default:
                    Console.WriteLine($"[client] ignore arg: {args[i]}");
                    break;
            }
        }

        IPAddress? forceMatchIp = null;
        if (forceMatchIpRaw is not null)
        {
            if (!IPAddress.TryParse(forceMatchIpRaw, out forceMatchIp))
            {
                Console.WriteLine($"[client] bad --match-ip {forceMatchIpRaw}");
                return;
            }
            Console.WriteLine($"[client] --match-ip {forceMatchIp} (override op9 advertised address; keep op9 port)");
        }

        foreach (var lan in LanInterfacePicker.GetLanIpv4Endpoints())
            Console.WriteLine($"[lan] KEEP {lan.InterfaceName}: {lan.Address} br={lan.Broadcast}");
        foreach (var skipped in LanInterfacePicker.DescribeSkippedInterfaces())
            Console.WriteLine($"[lan] SKIP {skipped}");

        IPEndPoint joinEp;
        if (forceIp is not null)
        {
            if (!IPAddress.TryParse(forceIp, out var ip))
            {
                Console.WriteLine($"[client] bad --ip {forceIp}");
                return;
            }
            joinEp = new IPEndPoint(ip, forcePort ?? LanDiscoveryHost.DefaultGamePort);
            Console.WriteLine($"[client] skip discovery — direct LiteNetLib lobby join {joinEp}");
        }
        else
        {
            await using var disc = new LanDiscoveryClient();
            disc.Start();

            // Interactive --pick waits the whole window so we can list every lobby that answered.
            // Auto (default) returns on the first reply for a snappy connect.
            var interactive = interactivePick && !autoPick && forceIndex is null && !Console.IsInputRedirected;
            Console.WriteLine(
                $"[client] probing discovery UDP {LanDiscoveryHost.DiscoveryPort} for {waitSeconds}s " +
                $"(hosting phone must be answering; join will use port from reply, usually {LanDiscoveryHost.DefaultGamePort}) …");
            var found = await disc.WaitForLobbiesAsync(
                TimeSpan.FromSeconds(waitSeconds), minCount: 1, waitFullWindow: interactive);

            // Silence discovery BEFORE printing the list / prompting so the RX/TX flood can't
            // drown stdin (root cause of the "select never joins" hang) and stops probing traffic.
            disc.Quiet();

            if (found.Count == 0)
            {
                Console.WriteLine();
                Console.WriteLine("[client] no lobbies found.");
                Console.WriteLine(
                    $"[client] stats: probes={disc.ProbesSent} rx={disc.DatagramsReceived} " +
                    $"echo={disc.EchoesIgnored} parseFail={disc.ParseFailures} ok={disc.RepliesReceived}");
                Console.WriteLine("[client] Checklist:");
                Console.WriteLine("  1) On the phone: CREATE a LAN lobby (host), do not only open search.");
                Console.WriteLine("  2) Same Wi‑Fi as this PC (VPN may hide NICs — see [lan] KEEP above).");
                Console.WriteLine("  3) If RX stays 0: phone never saw probes / firewall, or nothing is hosting on 5056.");
                Console.WriteLine("  4) If you see RX hex but parseFail: paste that hex — do not invent fields.");
                Console.WriteLine(
                    $"  5) Or skip discovery: --client --ip <phone-lan-ip> --port {LanDiscoveryHost.DefaultGamePort}");
                return;
            }

            Console.WriteLine();
            Console.WriteLine("Discovered lobbies:");
            for (var i = 0; i < found.Count; i++)
            {
                var L = found[i];
                Console.WriteLine(
                    $"  [{i}] '{L.Info.HostName}' / '{L.Info.LevelName}' " +
                    $"members={L.Info.MemberCount} → {L.JoinEndpoint}");
            }

            int pick;
            if (forceIndex is not null)
            {
                pick = forceIndex.Value;
                Console.WriteLine($"[client] --index {pick}");
            }
            else if (interactive && found.Count > 1)
            {
                pick = PromptForLobbyIndex(found.Count);
                if (pick < 0)
                {
                    Console.WriteLine("[client] cancelled");
                    return;
                }
            }
            else
            {
                // Default UX: auto-pick the first lobby so a bare `--client` never hangs forever.
                // Interactive selection is opt-in via --pick.
                pick = 0;
                var why = autoPick ? "--auto"
                    : Console.IsInputRedirected ? "stdin not a TTY"
                    : interactivePick ? "only one lobby"
                    : "default (pass --pick to choose manually)";
                Console.WriteLine($"[client] auto-pick index 0 ({why}) → {found[0].JoinEndpoint}");
            }

            if (pick < 0 || pick >= found.Count)
            {
                Console.WriteLine($"[client] bad index {pick}");
                return;
            }
            joinEp = found[pick].JoinEndpoint;
            if (forcePort is not null)
                joinEp = new IPEndPoint(joinEp.Address, forcePort.Value);
            Console.WriteLine($"[client] Connecting to lobby [{pick}] {joinEp} …");
        }

        using var client = new GameNetClient(
            profileName, saveCaptures: true, forceMatchIp: forceMatchIp, probeTeam: probeTeam);
        client.Connect(joinEp);

        Console.WriteLine(
            $"Logging host ops. probeTeam={probeTeam}" +
            (probeTeam is MatchTeam.Ct or MatchTeam.Tr
                ? " — after Found: team + catalog map spawn Ct_Ct/Tr_Tr + State (killable)."
                : " — after Found: Spectator.") +
            " On Play → follow op9 → Handshake → JoinRoom(Dedik).");
        Console.WriteLine("After Found: leave running — capture post-Found match RX. Captures → bin/.../captures/");
        Console.WriteLine("Commands: status|roster|quit");
        if (Console.IsInputRedirected)
        {
            await Task.Delay(Timeout.Infinite);
            return;
        }

        while (true)
        {
            var line = Console.ReadLine();
            if (line is null) break;
            var cmd = line.Trim().ToLowerInvariant();
            if (cmd is "quit" or "exit") break;
            if (cmd is "roster")
            {
                Console.WriteLine(client.Match?.DescribeRoster() ?? "[roster] match not connected yet");
                continue;
            }
            if (cmd is "status")
            {
                Console.WriteLine(
                    $"state={client.State} selfId={client.SelfMemberId?.ToString() ?? "(pending)"} lobby={joinEp} " +
                    $"hosting={client.LastHosting?.ToString() ?? "(none)"} " +
                    $"match={(client.Match?.IsConnected == true ? client.Match.Endpoint?.ToString() : "not connected")} " +
                    $"joinOk={client.Match?.JoinSucceeded ?? false} " +
                    $"postJoinRx={client.Match?.PostJoinRxCount ?? 0} " +
                    $"probeTeam={probeTeam} " +
                    $"appId='{client.Match?.AppId ?? MatchAuth.AppId}' " +
                    $"roster='{client.Match?.RosterRoom ?? profileName}'");
                if (client.Match is not null)
                    Console.WriteLine(client.Match.DescribeRoster());
                continue;
            }
            Console.WriteLine("status|roster|quit");
        }
    }
}

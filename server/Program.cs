using System.Net;
using StandChillow.LanServer;
using StandChillow.LanServer.Lan;
using StandChillow.LanServer.Net;
using StandChillow.LanServer.Net.Lobby;
using StandChillow.LanServer.Net.Match;

// CLI:
//   Dedicated host (default):  dotnet run -c Release [-- lobbyTitle]
//     start match: console `start`/`play` or phone chat `/play` `/start`
//     lobby chat: `/mode` `/map` (slash commands); player chat relayed with speaker id
//     defaults: Ranked2v2 + Sandstone 2x2; title «влал хостит рялна»
//   ConnectAsClient (learning only):  dotnet run -c Release -- --client …
// Captures under bin/.../captures/ are for analysis only — never replay into host replies.
//
// Port map (decompile + live):
//   UDP 5056 = LanDiscovery probe/reply only (Bonjour V2). Searching phones probe; hosting phones reply.
//   UDP 7778 = LiteNetLib lobby join (LanDiscoveryInfo.GamePort). Not used for Bonjour probes.
//   UDP 7777 = LiteNetLib game/match after OpGameHostingStateChangedEvent (esz).
//              Connect key = fwv.cwpw "MyVerySecretKey" (AcceptIfKey); ChannelsCount=3.

var logPath = RunLog.Start();
Console.WriteLine($"[log] writing to {logPath}");
Console.WriteLine($"[log] read from start: less '{logPath}'   or   tail -n 200 '{logPath}'");
Console.WriteLine();

if (IsClientMode(args, out var clientArgs))
{
    await RunConnectAsClientAsync(clientArgs);
    return;
}

await RunDedicatedHostAsync(args);

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

static async Task RunDedicatedHostAsync(string[] args)
{
    Console.Title = "StandChillow LAN Dedicated Lobby";
    Console.WriteLine("╔══════════════════════════════════════════════════════════╗");
    Console.WriteLine("║  StandChillow LAN dedicated host                       ║");
    Console.WriteLine("║  discovery UDP 5056  ·  lobby UDP 7778                 ║");
    Console.WriteLine("║  match UDP 7777 (Handshake + JoinRoom)                 ║");
    Console.WriteLine("║  default: Ranked2v2 / Sandstone 2x2 («союзники»)       ║");
    Console.WriteLine("║  probe: Bonjour V2 + sosalbolt482   proto: 4           ║");
    Console.WriteLine("╚══════════════════════════════════════════════════════════╝");
    Console.WriteLine();

    // Keep CLI simple: optional level/title only. Discovery HostName = LobbyId (live phone).
    var level = args.ElementAtOrDefault(0) ?? "влал хостит рялна";
    if (args.Length >= 2)
        level = args[1]; // legacy: [ignoredName] [level]

    foreach (var lan in LanInterfacePicker.GetLanIpv4Endpoints())
        Console.WriteLine($"[lan] KEEP {lan.InterfaceName}: {lan.Address} br={lan.Broadcast}");
    foreach (var skipped in LanInterfacePicker.DescribeSkippedInterfaces())
        Console.WriteLine($"[lan] SKIP {skipped}");

    // GameNetHost creates LobbySession (LobbyId) first; discovery HostName mirrors it.
    // Waiting lobby: cxbl=1 + Extra1=level + Extra2=mode → map thumbnail (LanDiscoveredLobbyView.Show).
    // In-progress: cxbl=0 (no extras) → Join / GameAlreadyStarted details (qti → hp.cu.iag), no map.
    var lobbyTitle = level;
    LanDiscoveryInfo current = new()
    {
        HostName = "pending",
        LevelName = lobbyTitle,
        MemberCount = 1,
        ProtocolVersion = LanDiscoveryHost.ProtocolVersion,
        GamePort = LanDiscoveryHost.DefaultGamePort,
        HasExtraStrings = true,
        Extra1 = LobbyPropKeys.DefaultSelectedLevel, // level id for thumbnail
        Extra2 = LobbyPropKeys.DefaultGameModeId,    // mode id for details
    };

    await using var discovery = new LanDiscoveryHost(current) { ReplyFromAllLanNics = true };
    discovery.Start();

    GameNetHost? gameRef = null;
    void SyncDiscovery()
    {
        if (gameRef is null) return;
        var n = (byte)Math.Clamp(1 + gameRef.JoinedCount, 1, 255);
        var inProgress = gameRef.MatchStarted;
        var mode = gameRef.Session.GameModeId;
        var map = gameRef.Session.SelectedLevels.Count > 0
            ? gameRef.Session.SelectedLevels[0]
            : LobbyPropKeys.DefaultSelectedLevel;

        // Decompile LanDiscoveredLobbyView: cxbl=false → no map image + Join/GameAlreadyStarted
        // details; cxbl=true + Extra1 level → map thumbnail (waiting lobby).
        var next = current with
        {
            MemberCount = n,
            LevelName = lobbyTitle,
            HasExtraStrings = !inProgress,
            Extra1 = inProgress ? "" : map,
            Extra2 = inProgress ? "" : mode,
        };
        if (next == current) return;
        current = next;
        discovery.UpdateInfo(current);
        Console.WriteLine(
            inProgress
                ? $"[discovery] IN PROGRESS — cxbl=0 (Join UI), members={n}"
                : $"[discovery] WAITING — cxbl=1 Extra1='{map}' Extra2='{mode}', members={n}");
    }

    var advertiseIp = LanInterfacePicker.PickLanBindAddress();
    using var game = new GameNetHost(
        lobbyTitle,
        LanDiscoveryHost.DefaultGamePort,
        discovery.BoundEndpoints,
        onRosterChanged: SyncDiscovery,
        advertiseLanIp: advertiseIp,
        onMatchStarted: SyncDiscovery);
    gameRef = game;

    // Live: discovery HostName == LobbyId property (32-char uppercase MD5 hex).
    current = current with { HostName = game.Session.LobbyId };
    discovery.UpdateInfo(current);
    SyncDiscovery();

    Console.WriteLine();
    Console.WriteLine($"Announcing lobbyId='{current.HostName}' / title='{lobbyTitle}'");
    Console.WriteLine($"  probe listen : UDP {LanDiscoveryHost.DiscoveryPort}");
    Console.WriteLine($"  lobby listen : UDP {current.GamePort}");
    Console.WriteLine($"  match listen : UDP {GameMatchHost.DefaultMatchPort} SINGLETON (key MyVerySecretKey)");
    Console.WriteLine($"  advertise    : {game.AdvertiseLanIp}:{GameMatchHost.DefaultMatchPort}");
    Console.WriteLine($"  mode/levels  : {game.Session.GameModeId} / [{string.Join(", ", game.Session.SelectedLevels)}]");
    Console.WriteLine($"  discovery    : cxbl={(current.HasExtraStrings ? 1 : 0)} (1=map waiting, 0=Join in-progress)");
    Console.WriteLine($"  payload hex  : {current.ToHex()}");
    Console.WriteLine("Phone: LAN list → join → chat /mode /map · /play|/start (or console start).");
    Console.WriteLine("Expect: op7 → SearchingStarted → op9 LAN:7777 → Handshake → JoinRoom Dedik.");
    Console.WriteLine("Second play/start: re-advertise in-progress only (no new 7777 bind).");
    Console.WriteLine("Captures: server/bin/Release/net8.0/captures/");
    Console.WriteLine("Commands: start|play|level|members|status|binds|roster|quit");

    if (Console.IsInputRedirected)
    {
        Console.WriteLine("[main] stdin redirected — running until SIGTERM");
        await Task.Delay(Timeout.Infinite);
    }
    else
    {
        var running = true;
        while (running)
        {
            var line = Console.ReadLine();
            if (line is null) break;
            var parts = line.Split(' ', 2, StringSplitOptions.TrimEntries);
            if (parts.Length == 0 || parts[0].Length == 0) continue;
            switch (parts[0].ToLowerInvariant())
            {
                case "quit":
                case "exit":
                    running = false;
                    break;
                case "start":
                case "play":
                    game.TryStartMatch("console");
                    break;
                case "level" when parts.Length == 2:
                    lobbyTitle = parts[1];
                    game.LobbyName = parts[1];
                    SyncDiscovery();
                    break;
                case "members" when parts.Length == 2 && byte.TryParse(parts[1], out var n):
                    current = current with { MemberCount = n };
                    discovery.UpdateInfo(current);
                    break;
                case "binds":
                    foreach (var e in discovery.BoundEndpoints)
                        Console.WriteLine($"  {e.InterfaceName} {e.Address} br={e.Broadcast}");
                    break;
                case "roster":
                    foreach (var (name, status) in game.Session.SnapshotRoster())
                        Console.WriteLine($"  {name} ({LobbySession.FormatStatus(status)})");
                    break;
                case "status":
                    SyncDiscovery();
                    Console.WriteLine(
                        $"lobbyId={current.HostName} level={current.LevelName} members={current.MemberCount} " +
                        $"cxbl={(current.HasExtraStrings ? 1 : 0)} extras=[{current.Extra1}|{current.Extra2}] " +
                        $"mode={game.Session.GameModeId} levels=[{string.Join(", ", game.Session.SelectedLevels)}] " +
                        $"advPort={current.GamePort} match={game.AdvertiseLanIp}:{GameMatchHost.DefaultMatchPort} " +
                        $"matchStarted={game.MatchStarted} matchListening={game.Match.IsListening} " +
                        $"matchPeers={game.Match.PeerCount} " +
                        $"dg={discovery.DatagramsReceived} probes={discovery.ProbesMatched} " +
                        $"replies={discovery.RepliesSent} err={discovery.ReplyErrors} " +
                        $"netConnect={game.ConnectRequests} unconn={game.UnconnectedMessages} " +
                        $"peers={game.PeerCount} joined={game.JoinedCount}");
                    break;
                default:
                    Console.WriteLine("start|play|level|members|status|binds|roster|quit");
                    break;
            }
        }
    }
}

static async Task RunConnectAsClientAsync(string[] args)
{
    Console.Title = "StandChillow LAN ConnectAsClient";
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
    Console.WriteLine("      Skip discovery: --ip <phone> --port 7778");
    Console.WriteLine("      Force match IP: --match-ip <lan-ip> (keeps op9 port, usually 7777)");
    Console.WriteLine();

    var profileName = "probe";
    var autoPick = false;
    string? forceIp = null;
    string? forceMatchIpRaw = null;
    int? forcePort = null;
    int? forceIndex = null;
    var waitSeconds = 12;

    for (var i = 0; i < args.Length; i++)
    {
        switch (args[i])
        {
            case "--auto":
                autoPick = true;
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
        Console.WriteLine(
            $"[client] probing discovery UDP {LanDiscoveryHost.DiscoveryPort} for {waitSeconds}s " +
            $"(hosting phone must be answering; join will use port from reply, usually {LanDiscoveryHost.DefaultGamePort}) …");
        var found = await disc.WaitForLobbiesAsync(TimeSpan.FromSeconds(waitSeconds), minCount: 1);
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
        }
        else if (autoPick || Console.IsInputRedirected)
        {
            pick = 0;
            Console.WriteLine("[client] auto-pick index 0");
        }
        else
        {
            Console.Write("Select index (default 0): ");
            var line = Console.ReadLine();
            pick = int.TryParse(line, out var n) ? n : 0;
        }

        if (pick < 0 || pick >= found.Count)
        {
            Console.WriteLine($"[client] bad index {pick}");
            return;
        }
        joinEp = found[pick].JoinEndpoint;
        if (forcePort is not null)
            joinEp = new IPEndPoint(joinEp.Address, forcePort.Value);
    }

    using var client = new GameNetClient(profileName, saveCaptures: true, forceMatchIp: forceMatchIp);
    client.Connect(joinEp);

    Console.WriteLine("Logging host ops. On Play → follow op9 → Handshake → JoinRoom(Dedik).");
    Console.WriteLine("After Found: leave running — capture post-Found match RX. Captures → bin/.../captures/");
    Console.WriteLine("Commands: status|quit");
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
        if (cmd is "status")
        {
            Console.WriteLine(
                $"state={client.State} selfId={client.SelfMemberId?.ToString() ?? "(pending)"} lobby={joinEp} " +
                $"hosting={client.LastHosting?.ToString() ?? "(none)"} " +
                $"match={(client.Match?.IsConnected == true ? client.Match.Endpoint?.ToString() : "not connected")} " +
                $"joinOk={client.Match?.JoinSucceeded ?? false} " +
                $"postJoinRx={client.Match?.PostJoinRxCount ?? 0} " +
                $"appId='{client.Match?.AppId ?? MatchAuth.AppId}' " +
                $"roster='{client.Match?.RosterRoom ?? profileName}'");
        }
        else
            Console.WriteLine("status|quit");
    }
}

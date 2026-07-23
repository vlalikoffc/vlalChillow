using System.Net;
using StandChillow.LanServer;
using StandChillow.LanServer.Dashboard;
using StandChillow.LanServer.Lan;
using StandChillow.LanServer.Net;
using StandChillow.LanServer.Net.Lobby;
using StandChillow.LanServer.Net.Match;
using StandChillow.LanServer.Plugins;

// CLI:
//   Dedicated host (default):  dotnet run -c Release [--debug-chat] [-- lobbyTitle]
//     start match: console `start`/`play` or phone chat `/play` `/start`
//     lobby chat: `/mode` `/map` `/set` (slash commands); player chat relayed with speaker id
//     --debug-chat / DEBUG_MATCH_CHAT=1 → Server posts plant/kill/round-end lines into chat
//     defaults: Ranked2v2 + Sandstone 2x2; Allies first-to-8; title «влал хостит рялна»
//   ConnectAsClient (learning only):  dotnet run -c Release -- --client …
// Captures under bin/.../captures/ are for analysis only — never replay into host replies.
//
// Port map (decompile + live):
//   UDP 5056 = LanDiscovery probe/reply only (Bonjour V2). Searching phones probe; hosting phones reply.
//   UDP 7778 = LiteNetLib lobby join (LanDiscoveryInfo.GamePort). Not used for Bonjour probes.
//   UDP 7777 = LiteNetLib game/match after OpGameHostingStateChangedEvent (esz).
//              Connect key = fwv.cwpw "MyVerySecretKey" (AcceptIfKey); ChannelsCount=3.

if (IsClientMode(args, out var clientArgs))
{
    var clientLog = RunLog.Start(dashboardMode: false);
    Console.WriteLine($"[log] writing to {clientLog}");
    Console.WriteLine($"[log] read from start: less '{clientLog}'   or   tail -n 200 '{clientLog}'");
    Console.WriteLine();
    await RunConnectAsClientAsync(clientArgs);
    return;
}

// Dedicated host: own the terminal with the CLI dashboard whenever stdout is a real console.
// In dashboard mode all normal Console.WriteLine logging is routed to latest.log only.
var useDashboard = !Console.IsOutputRedirected;
var logPath = RunLog.Start(dashboardMode: useDashboard);
if (!useDashboard)
{
    Console.WriteLine($"[log] writing to {logPath}");
    Console.WriteLine($"[log] read from start: less '{logPath}'   or   tail -n 200 '{logPath}'");
    Console.WriteLine();
}

ConsumeDebugMatchChatFlag(ref args);
await RunDedicatedHostAsync(args, useDashboard);

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
/// Enable <see cref="MatchHostSettings.DebugMatchChat"/> from <c>--debug-chat</c> /
/// <c>--debug-match-chat</c> or env <c>DEBUG_MATCH_CHAT=1|true|yes|on</c>. Strips those
/// flags from <paramref name="args"/> so they are not used as lobby title.
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

static async Task RunDedicatedHostAsync(string[] args, bool useDashboard)
{
    Console.Title = "влалChillow LAN Dedicated Lobby";
    Console.WriteLine("╔══════════════════════════════════════════════════════════╗");
    Console.WriteLine("║  влалChillow LAN dedicated host                        ║");
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

    using var plugins = new PluginManager();
    Console.WriteLine($"[plugins] root: {plugins.PluginsRoot}");

    var advertiseIp = LanInterfacePicker.PickLanBindAddress();
    using var game = new GameNetHost(
        lobbyTitle,
        LanDiscoveryHost.DefaultGamePort,
        discovery.BoundEndpoints,
        onRosterChanged: SyncDiscovery,
        advertiseLanIp: advertiseIp,
        onMatchStarted: () =>
        {
            SyncDiscovery();
            plugins.NotifyMatchStarted("host");
        });
    gameRef = game;
    plugins.AttachHost(game);
    plugins.LoadAll();

    using var pluginTickCts = new CancellationTokenSource();
    var pluginTick = Task.Run(async () =>
    {
        while (!pluginTickCts.IsCancellationRequested)
        {
            plugins.Tick();
            try { await Task.Delay(500, pluginTickCts.Token); }
            catch (OperationCanceledException) { break; }
        }
    });

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
    Console.WriteLine("Phone: LAN list → join → chat /mode /map /set · /play (launch) · /set start (WarmUp).");
    Console.WriteLine("Expect: op7 → SearchingStarted → op9 LAN:7777 → Handshake → JoinRoom Dedik.");
    Console.WriteLine("After both teams: /set start | set start | start | startmatch → WarmUp.");
    Console.WriteLine("Rematch: after MatchResults wait 5s → lobby idle (7777 stopped); /play then /set start.");
    Console.WriteLine("Captures: server/bin/Release/net8.0/captures/");
    Console.WriteLine("Commands: play|start|set start|startmatch|mode|map|status|binds|roster|plugins|quit");
    Console.WriteLine($"  defaults: {MatchHostSettings.FormatStatusLine()}");
    if (MatchHostSettings.DebugMatchChat)
        Console.WriteLine("  debug-chat: ON — Server posts plant/kill/round-end into lobby chat");

    // Command handler shared by the dashboard input line and the plain stdin loop.
    // Feedback goes to the dashboard notice/chat area when active, else to the console.
    CliDashboard? dashboard = null;
    void Feedback(string text)
    {
        if (dashboard is not null) dashboard.PostLocalNotice(text);
        else Console.WriteLine(text);
    }

    bool HandleCommand(string line)
    {
        var trimmed = line.Trim();
        if (trimmed.Length == 0) return true;

        // Slash form: /set start, /play, … — same as phone chat (silent command, no chat bubble).
        if (trimmed.StartsWith('/'))
        {
            var body = trimmed[1..].Trim();
            if (body.Length == 0) return true;
            return HandleCommand(body);
        }

        var parts = trimmed.Split(' ', 2, StringSplitOptions.TrimEntries);
        if (parts.Length == 0 || parts[0].Length == 0) return true;
        switch (parts[0].ToLowerInvariant())
        {
            case "quit":
            case "exit":
                return false;
            case "play":
                // Explicit rematch / first launch — may HardResetMatchForNewStart.
                game.TryStartMatch("console");
                break;
            case "start":
            case "startmatch":
                // Arm WarmUp when match already up; first launch only if never Play'd.
                // Never HardReset (that was the `start`/`stat` teardown bug).
                game.TryArmWarmupOrFirstPlay("console");
                break;
            case "stat":
                // Typo for status — never chat/teardown (latest.log: `stat` was broadcast then
                // a follow-up `start` tore down the live WaitingPlayers room).
                goto case "status";
            case "mode" when parts.Length == 2:
                if (GameModeCatalog.TryResolveMode(parts[1], out var m))
                {
                    game.Session.GameModeId = m.GameModeId;
                    game.Session.SelectedLevels = new[] { GameModeCatalog.DefaultLevelFor(m.GameModeId) };
                    SyncDiscovery();
                    Feedback($"mode → {m.GameModeId} / {game.Session.SelectedLevels[0]}");
                }
                else Feedback($"unknown mode '{parts[1]}'");
                break;
            case "map" when parts.Length == 2:
            case "level" when parts.Length == 2 && parts[1].Contains(' '):
                game.Session.SelectedLevels = new[] { parts[1] };
                SyncDiscovery();
                Feedback($"map → {parts[1]}");
                break;
            case "title" when parts.Length == 2:
                lobbyTitle = parts[1];
                game.LobbyName = parts[1];
                SyncDiscovery();
                Feedback($"title → {parts[1]}");
                break;
            case "members" when parts.Length == 2 && byte.TryParse(parts[1], out var n):
                current = current with { MemberCount = n };
                discovery.UpdateInfo(current);
                break;
            case "binds":
                foreach (var e in discovery.BoundEndpoints)
                    Feedback($"{e.InterfaceName} {e.Address} br={e.Broadcast}");
                break;
            case "roster":
                foreach (var (name, status) in game.Session.SnapshotRoster())
                    Feedback($"{name} ({LobbySession.FormatStatus(status)})");
                break;
            case "status":
                SyncDiscovery();
                Feedback(
                    $"members={current.MemberCount} mode={game.Session.GameModeId} " +
                    $"match={game.MatchStarted} peers={game.PeerCount} joined={game.JoinedCount} " +
                    $"matchPeers={game.Match.PeerCount}");
                Feedback(MatchHostSettings.FormatStatusLine());
                break;
            case "set":
                HandleConsoleSet(parts.Length > 1 ? parts[1] : "", Feedback, game);
                break;
            case "plugins":
            case "plugin":
            {
                var sub = parts.Length > 1 ? parts[1].Trim() : "";
                if (sub.Equals("reload", StringComparison.OrdinalIgnoreCase))
                {
                    var n = plugins.Reload();
                    Feedback($"[plugins] reloaded → {n} ({string.Join(", ", plugins.LoadedNames)})");
                }
                else
                {
                    var names = plugins.LoadedNames;
                    Feedback(
                        names.Count == 0
                            ? $"[plugins] none loaded (drop DLLs in {plugins.PluginsRoot})"
                            : $"[plugins] loaded: {string.Join(", ", names)}");
                    Feedback("usage: plugins | plugins reload");
                }
                break;
            }
            default:
            {
                // Plugin console commands first; else free text → lobby chat as Server.
                var cmd = parts[0];
                var argsRest = parts.Length > 1 ? parts[1] : "";
                if (plugins.TryHandleConsoleCommand(cmd, argsRest, Feedback))
                    break;
                game.BroadcastConsoleChat(trimmed);
                break;
            }
        }
        return true;
    }

    static void HandleConsoleSet(string args, Action<string> feedback, GameNetHost game)
    {
        if (string.IsNullOrWhiteSpace(args)
            || args.Equals("help", StringComparison.OrdinalIgnoreCase))
        {
            feedback(MatchHostSettings.FormatSetHelp().Replace("/set", "set"));
            feedback(MatchHostSettings.FormatStatusLine());
            return;
        }

        var bits = args.Split(' ', 2, StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        var key = bits[0];
        var val = bits.Length > 1 ? bits[1] : "";
        if (key.Equals("status", StringComparison.OrdinalIgnoreCase))
        {
            feedback(MatchHostSettings.FormatStatusLine());
            return;
        }

        if (key.Equals("start", StringComparison.OrdinalIgnoreCase)
            || key.Equals("startmatch", StringComparison.OrdinalIgnoreCase))
        {
            game.ArmMatchStart("console");
            return;
        }

        if (key.Equals("round", StringComparison.OrdinalIgnoreCase)
            || key.Equals("rounds", StringComparison.OrdinalIgnoreCase))
        {
            if (!int.TryParse(val, out var n) || n < 1)
            {
                feedback("usage: set round <N>");
                return;
            }

            MatchHostSettings.TotalRounds = n;
            feedback(
                $"rounds → MR-{MatchHostSettings.TotalRounds} " +
                $"(Escalation first to {MatchHostSettings.TotalRounds / 2 + 1})");
            return;
        }

        if (key.Equals("wins", StringComparison.OrdinalIgnoreCase)
            || key.Equals("win", StringComparison.OrdinalIgnoreCase))
        {
            if (!int.TryParse(val, out var n) || n < 1)
            {
                feedback("usage: set wins <N>");
                return;
            }

            MatchHostSettings.WinsNeeded = n;
            feedback($"wins → first-to-{MatchHostSettings.WinsNeeded} (Allies)");
            return;
        }

        if (key.Equals("money", StringComparison.OrdinalIgnoreCase)
            && int.TryParse(val, out var money))
        {
            MatchHostSettings.RoundStartMoney = money;
            feedback($"money → {MatchHostSettings.RoundStartMoney}");
            return;
        }

        if (key.Equals("fuse", StringComparison.OrdinalIgnoreCase) && int.TryParse(val, out var fuse))
        {
            MatchHostSettings.BombFuseSeconds = fuse;
            feedback($"fuse → {MatchHostSettings.BombFuseSeconds}s");
            return;
        }

        if (key.Equals("prep", StringComparison.OrdinalIgnoreCase) && int.TryParse(val, out var prep))
        {
            MatchHostSettings.PrepSeconds = prep;
            feedback($"prep → {MatchHostSettings.PrepSeconds}s");
            return;
        }

        if (key.Equals("prestart", StringComparison.OrdinalIgnoreCase) && int.TryParse(val, out var ps))
        {
            MatchHostSettings.PreStartSeconds = ps;
            feedback($"prestart → {MatchHostSettings.PreStartSeconds}s");
            return;
        }

        if (key.Equals("warmup", StringComparison.OrdinalIgnoreCase) && int.TryParse(val, out var wu))
        {
            MatchHostSettings.WarmupSeconds = wu;
            feedback($"warmup → {MatchHostSettings.WarmupSeconds}s");
            return;
        }

        if (key.Equals("roundtime", StringComparison.OrdinalIgnoreCase) && int.TryParse(val, out var rt))
        {
            MatchHostSettings.RoundSeconds = rt;
            feedback($"roundtime → {MatchHostSettings.RoundSeconds}s");
            return;
        }

        if (key.Equals("pause", StringComparison.OrdinalIgnoreCase) && int.TryParse(val, out var pause))
        {
            MatchHostSettings.RoundEndPauseSeconds = pause;
            feedback($"pause → {MatchHostSettings.RoundEndPauseSeconds}s");
            return;
        }

        feedback("set help | set start | set round N | set money N | set fuse|prep|prestart|warmup|roundtime|pause N");
    }

    try
    {
        if (useDashboard)
        {
            dashboard = new CliDashboard(game);
            dashboard.Start();
            try
            {
                if (Console.IsInputRedirected)
                {
                    // No keyboard, but keep the live dashboard on screen until SIGTERM.
                    await Task.Delay(Timeout.Infinite);
                }
                else
                {
                    while (true)
                    {
                        var line = dashboard.NextCommand();
                        if (line is null || !HandleCommand(line))
                            break;
                    }
                }
            }
            finally
            {
                dashboard.Stop();
            }
        }
        else if (Console.IsInputRedirected)
        {
            Console.WriteLine("[main] stdin redirected — running until SIGTERM");
            await Task.Delay(Timeout.Infinite);
        }
        else
        {
            while (true)
            {
                var line = Console.ReadLine();
                if (line is null || !HandleCommand(line))
                    break;
            }
        }
    }
    finally
    {
        pluginTickCts.Cancel();
        try { await pluginTick.WaitAsync(TimeSpan.FromSeconds(2)); }
        catch { /* ignore */ }
    }
}

// Interactive lobby picker (only used with --pick and a real TTY). Discovery is already
// quieted by the caller, so the prompt is readable. Enter = index 0; a valid number joins
// immediately; q/quit cancels; anything else re-prompts (never hangs silently).
static int PromptForLobbyIndex(int count)
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

static async Task RunConnectAsClientAsync(string[] args)
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
    Console.WriteLine("      Fighting probe: --team ct|tr  (default Spectator; Ct/Tr spawn killable pawn)");
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
            ? " — after Found: team + Ct_Ct/Tr_Tr pawn + State (killable)."
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

using System.Net;
using System.Runtime.InteropServices;
using StandChillow.LanServer.Dashboard;
using StandChillow.LanServer.Lan;
using StandChillow.LanServer.Net;
using StandChillow.LanServer.Net.Lobby;
using StandChillow.LanServer.Net.Match;
using StandChillow.LanServer.Plugins;

namespace StandChillow.LanServer.Startup;

/// <summary>Constructs and owns the dedicated discovery, lobby, match, and plugin lifecycle.</summary>
public static class HostBootstrap
{
    public static async Task RunAsync(string[] args, bool useDashboard)
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
        Console.WriteLine("Phone: LAN list → join → chat /mode /map /set · /play · /start · /end · /help");
        Console.WriteLine("Expect: op7 → SearchingStarted → op9 LAN:7777 → Handshake → JoinRoom Dedik.");
        Console.WriteLine("After both teams: /set start | set start | start | startmatch → WarmUp.");
        Console.WriteLine("Rematch: after MatchResults wait 5s → lobby idle (7777 stopped); /play then /set start.");
        Console.WriteLine("Early end: /end | set end | stopmatch — immediate teardown (no 5s).");
        Console.WriteLine("Captures: server/bin/Release/net8.0/captures/");
        Console.WriteLine("Commands: play|start|end|mode|map|set|help|status|binds|roster|plugins|quit");
        Console.WriteLine($"  defaults: {MatchHostSettings.FormatStatusLine()}");
        if (MatchHostSettings.DebugMatchChat)
            Console.WriteLine("  debug-chat: ON — Server posts plant/kill/round-end into lobby chat");

        // Command handler shared by the dashboard input line and the plain stdin loop.
        // Feedback goes to the dashboard notice/chat area when active, else to the console.
        CliDashboard? dashboard = null;
        using var shutdownCts = new CancellationTokenSource();
        PosixSignalRegistration? sigTermReg = null;

        void Feedback(string text)
        {
            if (dashboard is not null) dashboard.PostLocalNotice(text);
            else Console.WriteLine(text);
        }

        void RequestGracefulShutdown(string reason)
        {
            Console.WriteLine($"[main] {reason} — graceful shutdown (same as quit)…");
            try { shutdownCts.Cancel(); } catch { /* ignore */ }
            dashboard?.RequestQuit();

            // Plain Console.ReadLine does not unblock when CancelKeyPress sets e.Cancel=true.
            // Dispose hosts (idempotent) so LiteNetLib disconnects flush, then Exit.
            if (!useDashboard && !Console.IsInputRedirected)
            {
                try { pluginTickCts.Cancel(); } catch { /* ignore */ }
                try { game.Dispose(); } catch { /* ignore */ }
                try
                {
                    discovery.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(2));
                }
                catch { /* ignore */ }
                Environment.Exit(0);
            }
        }

        void OnCancelKeyPress(object? sender, ConsoleCancelEventArgs e)
        {
            e.Cancel = true;
            RequestGracefulShutdown("CTRL+C / CancelKeyPress");
        }

        Console.CancelKeyPress += OnCancelKeyPress;
        try
        {
            sigTermReg = PosixSignalRegistration.Create(
                PosixSignal.SIGTERM,
                _ => RequestGracefulShutdown("SIGTERM"));
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[main] SIGTERM registration skipped: {ex.Message}");
        }

        var commandRouter = new CommandRouter(
            game,
            plugins,
            discovery,
            getDiscoveryInfo: () => current,
            setDiscoveryInfo: value => current = value,
            setLobbyTitle: title =>
            {
                lobbyTitle = title;
                game.LobbyName = title;
                SyncDiscovery();
            },
            syncDiscovery: SyncDiscovery,
            feedback: Feedback);

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
                        // No keyboard, but keep the live dashboard on screen until SIGTERM/CTRL+C.
                        try { await Task.Delay(Timeout.Infinite, shutdownCts.Token); }
                        catch (OperationCanceledException) { /* graceful shutdown */ }
                    }
                    else
                    {
                        while (true)
                        {
                            var line = dashboard.NextCommand();
                            if (line is null || !commandRouter.Handle(line))
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
                Console.WriteLine("[main] stdin redirected — running until SIGTERM/CTRL+C");
                try { await Task.Delay(Timeout.Infinite, shutdownCts.Token); }
                catch (OperationCanceledException) { /* graceful shutdown */ }
            }
            else
            {
                while (!shutdownCts.IsCancellationRequested)
                {
                    var line = Console.ReadLine();
                    if (line is null || !commandRouter.Handle(line))
                        break;
                }
            }
        }
        finally
        {
            Console.CancelKeyPress -= OnCancelKeyPress;
            try { sigTermReg?.Dispose(); } catch { /* ignore */ }
            pluginTickCts.Cancel();
            try { await pluginTick.WaitAsync(TimeSpan.FromSeconds(2)); }
            catch { /* ignore */ }
        }
    }
}

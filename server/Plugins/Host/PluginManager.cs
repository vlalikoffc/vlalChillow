using System.Collections.Concurrent;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using StandChillow.LanServer.Dashboard;
using StandChillow.LanServer.Net;
using StandChillow.LanServer.Net.Lobby;
using StandChillow.LanServer.Net.Match;
using StandChillow.Server.Plugins;

namespace StandChillow.LanServer.Plugins;

/// <summary>
/// Loads plugin DLLs from <c>{BaseDirectory}/plugins/</c>, isolates failures, and bridges
/// host display events into <see cref="IPluginEvents"/>. Does not touch match opcodes.
/// </summary>
public sealed class PluginManager : IDisposable
{
    public static readonly JsonSerializerOptions StatusJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false,
    };

    private readonly string _pluginsRoot;
    private readonly PluginEventBus _events = new();
    private readonly PluginHostServices _hostServices;
    private readonly object _gate = new();
    private readonly List<LoadedPlugin> _loaded = new();
    private readonly ConcurrentDictionary<string, RegisteredCommand> _consoleCommands = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, RegisteredCommand> _chatCommands = new(StringComparer.OrdinalIgnoreCase);

    private GameNetHost? _game;
    private DateTime? _matchStartedUtc;
    private HashSet<string> _prevLobbyNames = new(StringComparer.Ordinal);
    private string? _prevPhase;
    private Dictionary<byte, string> _prevTeams = new();
    private bool _prevMatchStarted;
    private bool _hubSubscribed;
    private bool _disposed;

    public PluginManager(string? pluginsRoot = null)
    {
        _pluginsRoot = pluginsRoot
            ?? Path.Combine(AppContext.BaseDirectory, "plugins");
        Directory.CreateDirectory(_pluginsRoot);
        _hostServices = new PluginHostServices(() => _game, () => _matchStartedUtc);
    }

    public string PluginsRoot => _pluginsRoot;
    public IPluginEvents Events => _events;
    public IReadOnlyList<string> LoadedNames
    {
        get { lock (_gate) return _loaded.Select(p => p.Instance.Name).ToList(); }
    }

    public void AttachHost(GameNetHost game)
    {
        _game = game;
        game.PluginChatCommandHandler = TryHandleChatCommand;
        EnsureHubSubscriptions();
        _prevLobbyNames = SnapshotLobbyNames(game);
        _prevMatchStarted = game.MatchStarted;
        if (game.MatchStarted)
            _matchStartedUtc ??= DateTime.UtcNow;
    }

    public int LoadAll()
    {
        UnloadAll(silent: true);

        string[] dlls;
        try
        {
            dlls = Directory.GetFiles(_pluginsRoot, "*.dll", SearchOption.TopDirectoryOnly);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[plugins] cannot list {_pluginsRoot}: {ex.Message}");
            return 0;
        }

        // Skip the shared API if someone copied it into plugins/
        dlls = dlls
            .Where(p => !string.Equals(
                Path.GetFileName(p),
                "StandChillow.Server.Plugins.dll",
                StringComparison.OrdinalIgnoreCase))
            .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (dlls.Length == 0)
        {
            Console.WriteLine($"[plugins] folder ready: {_pluginsRoot} (no DLLs yet)");
            return 0;
        }

        var count = 0;
        foreach (var path in dlls)
        {
            if (TryLoadAssembly(path))
                count++;
        }

        Console.WriteLine(
            count > 0
                ? $"[plugins] loaded {count}: {string.Join(", ", LoadedNames)}"
                : $"[plugins] no IServerPlugin types found under {_pluginsRoot}");
        return count;
    }

    public int Reload()
    {
        Console.WriteLine("[plugins] reload…");
        return LoadAll();
    }

    public void Tick()
    {
        if (_disposed || _game is null) return;
        try { DiffAndRaise(); }
        catch (Exception ex)
        {
            Console.WriteLine($"[plugins] tick: {ex.Message}");
        }
    }

    /// <summary>Console / dashboard command path. Returns true if a plugin handled it.</summary>
    public bool TryHandleConsoleCommand(string command, string args, Action<string> reply)
    {
        if (!_consoleCommands.TryGetValue(command, out var reg))
            return false;
        return InvokeCommand(reg, command, args, "console", fromConsole: true, reply);
    }

    /// <summary>In-game slash (after host builtins). Returns true if handled.</summary>
    public bool TryHandleChatCommand(string speaker, string command, string args)
    {
        if (!_chatCommands.TryGetValue(command, out var reg))
            return false;
        return InvokeCommand(reg, command, args, speaker, fromConsole: false, reply: msg =>
        {
            try { _game?.BroadcastConsoleChat($"[{reg.PluginName}] {msg}"); }
            catch { /* ignore */ }
        });
    }

    internal void RegisterCommand(
        string pluginName,
        string name,
        PluginCommandHandler handler,
        string? help,
        bool console,
        bool chat)
    {
        name = name.Trim().TrimStart('/');
        if (name.Length == 0) return;
        var reg = new RegisteredCommand(pluginName, name, handler, help);
        if (console)
            _consoleCommands[name] = reg;
        if (chat)
            _chatCommands[name] = reg;
    }

    public void NotifyMatchStarted(string? reason)
    {
        _matchStartedUtc = DateTime.UtcNow;
        _prevMatchStarted = true;
        var status = _hostServices.GetStatus();
        SafeRaise(() => _events.RaiseMatchStarted(new MatchLifecycleEventArgs
        {
            ModeId = status.Mode.Id,
            Map = status.Map,
            Reason = reason,
        }));
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        UnloadAll(silent: false);
        if (_hubSubscribed)
        {
            DashboardHub.Chat -= OnHubChat;
            DashboardHub.Death -= OnHubDeath;
            DashboardHub.RoundResult -= OnHubRoundResult;
            _hubSubscribed = false;
        }
        if (_game is not null)
            _game.PluginChatCommandHandler = null;
    }

    private bool TryLoadAssembly(string path)
    {
        PluginAssemblyLoadContext? alc = null;
        try
        {
            alc = new PluginAssemblyLoadContext(path);
            var asm = alc.LoadFromAssemblyPath(path);
            var types = asm.GetExportedTypes()
                .Where(t => typeof(IServerPlugin).IsAssignableFrom(t)
                            && t is { IsInterface: false, IsAbstract: false })
                .ToList();

            if (types.Count == 0)
            {
                Console.WriteLine($"[plugins] skip {Path.GetFileName(path)} (no IServerPlugin)");
                alc.Unload();
                return false;
            }

            var sharedAlc = alc;
            alc = null; // unload only via LoadedPlugin set or !any path below
            var any = false;
            foreach (var type in types)
            {
                IServerPlugin? instance = null;
                try
                {
                    instance = (IServerPlugin)Activator.CreateInstance(type)!;
                    var dataDir = Path.Combine(_pluginsRoot, SanitizeFolder(instance.Name));
                    Directory.CreateDirectory(dataDir);
                    var log = new PluginLog(instance.Name);
                    var ctx = new PluginContext(
                        this, instance.Name, _pluginsRoot, dataDir, log, _hostServices, _events);

                    instance.OnLoad(ctx);
                    lock (_gate)
                    {
                        _loaded.Add(new LoadedPlugin(instance, sharedAlc, path));
                    }
                    any = true;
                    var ver = string.IsNullOrWhiteSpace(instance.Version) ? "" : $" v{instance.Version}";
                    Console.WriteLine($"[plugins] loaded {instance.Name}{ver} ← {Path.GetFileName(path)}");
                }
                catch (Exception ex)
                {
                    Console.WriteLine(
                        $"[plugins] failed {type.FullName} from {Path.GetFileName(path)}: {ex.Message}");
                    try { instance?.OnUnload(); } catch { /* ignore */ }
                }
            }

            if (!any)
            {
                try { sharedAlc.Unload(); } catch { /* ignore */ }
            }
            return any;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[plugins] load error {Path.GetFileName(path)}: {ex.Message}");
            try { alc?.Unload(); } catch { /* ignore */ }
            return false;
        }
    }

    private void UnloadAll(bool silent)
    {
        List<LoadedPlugin> copy;
        lock (_gate)
        {
            copy = _loaded.ToList();
            _loaded.Clear();
        }

        _consoleCommands.Clear();
        _chatCommands.Clear();

        var contexts = new HashSet<PluginAssemblyLoadContext>();
        foreach (var p in copy)
        {
            try { p.Instance.OnUnload(); }
            catch (Exception ex)
            {
                if (!silent)
                    Console.WriteLine($"[plugins] OnUnload {p.Instance.Name}: {ex.Message}");
            }
            contexts.Add(p.LoadContext);
        }

        foreach (var alc in contexts)
        {
            try { alc.Unload(); }
            catch { /* ignore */ }
        }

        if (!silent && copy.Count > 0)
            Console.WriteLine($"[plugins] unloaded {copy.Count}");

        // Give GC a nudge for collectible contexts (best-effort hot reload).
        GC.Collect();
        GC.WaitForPendingFinalizers();
    }

    private void EnsureHubSubscriptions()
    {
        if (_hubSubscribed) return;
        DashboardHub.Chat += OnHubChat;
        DashboardHub.Death += OnHubDeath;
        DashboardHub.RoundResult += OnHubRoundResult;
        _hubSubscribed = true;
    }

    private void OnHubChat(DashboardHub.ChatEntry e)
    {
        // Slash commands are silent and never posted; still filter leading /
        if (e.Text.StartsWith('/')) return;
        SafeRaise(() => _events.RaiseChat(new ChatEventArgs
        {
            Speaker = e.Speaker,
            Text = e.Text,
            FromServer = e.FromServer,
        }));
    }

    private void OnHubDeath(DashboardHub.DeathEntry e)
    {
        SafeRaise(() => _events.RaiseDeath(new DeathEventArgs
        {
            VictimNr = e.VictimNr,
            VictimName = e.VictimName,
            VictimTeam = StatusSnapshotBuilder.TeamKeyPublic(e.VictimTeam),
            Source = e.Source,
        }));
    }

    private void OnHubRoundResult(DashboardHub.RoundResultEntry e)
    {
        var status = _hostServices.GetStatus();
        SafeRaise(() => _events.RaiseRoundEnded(new RoundEndEventArgs
        {
            Headline = e.Headline,
            WinnerTeam = StatusSnapshotBuilder.TeamKeyPublic(e.Winner),
            MvpNr = e.MvpNr,
            MvpName = e.MvpName,
            IsFinal = e.IsFinal,
            ScoreTr = status.Scores.GetValueOrDefault("tr"),
            ScoreCt = status.Scores.GetValueOrDefault("ct"),
        }));

        if (e.IsFinal)
        {
            SafeRaise(() => _events.RaiseMatchEnded(new MatchLifecycleEventArgs
            {
                ModeId = status.Mode.Id,
                Map = status.Map,
                Reason = e.Headline,
            }));
            _matchStartedUtc = null;
            _prevMatchStarted = false;
        }
    }

    private void DiffAndRaise()
    {
        var game = _game;
        if (game is null) return;

        // Lobby join/leave
        var nowNames = SnapshotLobbyNames(game);
        foreach (var n in nowNames)
        {
            if (_prevLobbyNames.Contains(n)) continue;
            var status = game.Session.SnapshotRoster()
                .FirstOrDefault(r => r.Name == n).Status;
            SafeRaise(() => _events.RaisePlayerJoined(new PlayerLobbyEventArgs
            {
                Name = n,
                Status = status == PlayerLobbyStatus.InMatch ? "in_match" : "lobby",
            }));
        }
        foreach (var n in _prevLobbyNames)
        {
            if (nowNames.Contains(n)) continue;
            SafeRaise(() => _events.RaisePlayerLeft(new PlayerLobbyEventArgs { Name = n }));
        }
        _prevLobbyNames = nowNames;

        // Match start edge (in case Attach missed NotifyMatchStarted)
        if (game.MatchStarted && !_prevMatchStarted)
        {
            _matchStartedUtc ??= DateTime.UtcNow;
            _prevMatchStarted = true;
            var st = _hostServices.GetStatus();
            SafeRaise(() => _events.RaiseMatchStarted(new MatchLifecycleEventArgs
            {
                ModeId = st.Mode.Id,
                Map = st.Map,
                Reason = "detected",
            }));
        }
        else if (!game.MatchStarted && _prevMatchStarted)
        {
            _prevMatchStarted = false;
            _matchStartedUtc = null;
        }

        if (!game.MatchStarted)
        {
            _prevPhase = null;
            _prevTeams.Clear();
            return;
        }

        var match = game.Match.SnapshotActiveMatch();
        if (match is null) return;

        var phase = StatusSnapshotBuilder.MapPhase(match.Phase);
        if (!string.Equals(phase, _prevPhase, StringComparison.Ordinal))
        {
            var prev = _prevPhase;
            _prevPhase = phase;
            SafeRaise(() => _events.RaisePhaseChanged(new PhaseChangeEventArgs
            {
                PreviousPhase = prev,
                Phase = phase,
                Round = match.Round,
            }));
        }

        var nextTeams = new Dictionary<byte, string>();
        foreach (var a in match.Actors)
        {
            if (a.IsHost) continue;
            var key = StatusSnapshotBuilder.TeamKeyPublic(a.Team);
            nextTeams[a.Nr] = key;
            if (_prevTeams.TryGetValue(a.Nr, out var old) && old != key)
            {
                SafeRaise(() => _events.RaiseTeamChanged(new TeamChangeEventArgs
                {
                    Name = a.Name,
                    ActorNr = a.Nr,
                    Team = key,
                    PreviousTeam = old,
                }));
            }
        }
        _prevTeams = nextTeams;
    }

    private static HashSet<string> SnapshotLobbyNames(GameNetHost game)
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (name, _) in game.Session.SnapshotRoster())
        {
            if (string.Equals(name, LobbySession.ServerPlayerName, StringComparison.OrdinalIgnoreCase))
                continue;
            set.Add(name);
        }
        return set;
    }

    private bool InvokeCommand(
        RegisteredCommand reg,
        string command,
        string args,
        string speaker,
        bool fromConsole,
        Action<string> reply)
    {
        try
        {
            var ctx = new PluginCommandContext
            {
                Command = command,
                Args = args ?? "",
                Speaker = speaker,
                FromConsole = fromConsole,
                Log = new PluginLog(reg.PluginName),
                Reply = reply,
            };
            return reg.Handler(ctx);
        }
        catch (Exception ex)
        {
            Console.WriteLine(
                $"[plugins] command '{command}' ({reg.PluginName}) faulted: {ex.Message}");
            try { reply($"plugin error: {ex.Message}"); } catch { /* ignore */ }
            return true; // consumed, even if failed
        }
    }

    private static void SafeRaise(Action a)
    {
        try { a(); }
        catch (Exception ex)
        {
            Console.WriteLine($"[plugins] raise: {ex.Message}");
        }
    }

    private static string SanitizeFolder(string name)
    {
        foreach (var c in Path.GetInvalidFileNameChars())
            name = name.Replace(c, '_');
        return string.IsNullOrWhiteSpace(name) ? "plugin" : name.Trim();
    }

    private sealed record LoadedPlugin(IServerPlugin Instance, PluginAssemblyLoadContext LoadContext, string Path);
    private sealed record RegisteredCommand(string PluginName, string Name, PluginCommandHandler Handler, string? Help);
}

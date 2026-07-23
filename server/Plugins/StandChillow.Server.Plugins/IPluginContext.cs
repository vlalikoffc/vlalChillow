namespace StandChillow.Server.Plugins;

/// <summary>
/// Safe host surface for plugins. Display / admin only — no match opcode invention.
/// </summary>
public interface IPluginHost
{
    /// <summary>Broadcast a Server lobby/match chat line (same path as console free text).</summary>
    void BroadcastServerChat(string text);

    /// <summary>Full status snapshot matching tgbot <c>standchillow.status.v1</c>.</summary>
    PluginStatusSnapshot GetStatus();

    /// <summary>Explicit Play / rematch (may HardReset). Same as console <c>play</c>.</summary>
    bool TryStartMatch(string reason = "plugin");

    /// <summary>Arm WarmUp / first play without rematch teardown. Same as console <c>start</c>.</summary>
    bool TryArmWarmup(string reason = "plugin");
}

/// <summary>
/// Per-plugin context passed to <see cref="IServerPlugin.OnLoad"/>.
/// </summary>
public interface IPluginContext
{
    /// <summary>Root folder where plugin DLLs are loaded (<c>…/plugins</c>).</summary>
    string PluginsRoot { get; }

    /// <summary>
    /// Writable data directory for this plugin (<c>plugins/{Name}/</c>).
    /// Place <c>config.json</c> here; created on load.
    /// </summary>
    string DataDirectory { get; }

    IPluginLog Log { get; }
    IPluginHost Host { get; }
    IPluginEvents Events { get; }

    /// <summary>Register a console / dashboard command (no leading slash).</summary>
    void RegisterConsoleCommand(string name, PluginCommandHandler handler, string? help = null);

    /// <summary>
    /// Register an in-game slash command (name without <c>/</c>).
    /// Host built-ins (<c>mode</c>/<c>map</c>/<c>set</c>/<c>play</c>/<c>start</c>) win first.
    /// </summary>
    void RegisterChatCommand(string name, PluginCommandHandler handler, string? help = null);
}

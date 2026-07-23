using StandChillow.Server.Plugins;

namespace StandChillow.LanServer.Plugins;

internal sealed class PluginContext : IPluginContext
{
    private readonly PluginManager _manager;
    private readonly string _pluginName;

    public PluginContext(
        PluginManager manager,
        string pluginName,
        string pluginsRoot,
        string dataDirectory,
        IPluginLog log,
        IPluginHost host,
        IPluginEvents events)
    {
        _manager = manager;
        _pluginName = pluginName;
        PluginsRoot = pluginsRoot;
        DataDirectory = dataDirectory;
        Log = log;
        Host = host;
        Events = events;
    }

    public string PluginsRoot { get; }
    public string DataDirectory { get; }
    public IPluginLog Log { get; }
    public IPluginHost Host { get; }
    public IPluginEvents Events { get; }

    public void RegisterConsoleCommand(string name, PluginCommandHandler handler, string? help = null) =>
        _manager.RegisterCommand(_pluginName, name, handler, help, console: true, chat: false);

    public void RegisterChatCommand(string name, PluginCommandHandler handler, string? help = null) =>
        _manager.RegisterCommand(_pluginName, name, handler, help, console: false, chat: true);
}

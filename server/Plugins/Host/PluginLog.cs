using StandChillow.Server.Plugins;

namespace StandChillow.LanServer.Plugins;

internal sealed class PluginLog : IPluginLog
{
    private readonly string _prefix;

    public PluginLog(string pluginName) => _prefix = $"[plugins:{pluginName}]";

    public void Info(string message) => Console.WriteLine($"{_prefix} {message}");
    public void Warn(string message) => Console.WriteLine($"{_prefix} WARN {message}");
    public void Debug(string message) => Console.WriteLine($"{_prefix} {message}");

    public void Error(string message, Exception? ex = null)
    {
        if (ex is null)
            Console.WriteLine($"{_prefix} ERROR {message}");
        else
            Console.WriteLine($"{_prefix} ERROR {message}: {ex.GetType().Name}: {ex.Message}");
    }
}

using StandChillow.LanServer.Net;
using StandChillow.Server.Plugins;

namespace StandChillow.LanServer.Plugins;

internal sealed class PluginHostServices : IPluginHost
{
    private readonly Func<GameNetHost?> _game;
    private readonly Func<DateTime?> _matchStartedUtc;

    public PluginHostServices(Func<GameNetHost?> game, Func<DateTime?> matchStartedUtc)
    {
        _game = game;
        _matchStartedUtc = matchStartedUtc;
    }

    public void BroadcastServerChat(string text)
    {
        var g = _game();
        if (g is null || string.IsNullOrWhiteSpace(text)) return;
        try { g.BroadcastConsoleChat(text); }
        catch (Exception ex)
        {
            Console.WriteLine($"[plugins] BroadcastServerChat: {ex.Message}");
        }
    }

    public PluginStatusSnapshot GetStatus()
    {
        var g = _game();
        if (g is null)
        {
            return new PluginStatusSnapshot
            {
                Running = false,
                TsUtc = DateTime.UtcNow.ToString("o"),
                ServerName = "(stopped)",
            };
        }
        return StatusSnapshotBuilder.Build(g, _matchStartedUtc());
    }

    public bool TryStartMatch(string reason = "plugin")
    {
        var g = _game();
        if (g is null) return false;
        try { return g.TryStartMatch(reason); }
        catch (Exception ex)
        {
            Console.WriteLine($"[plugins] TryStartMatch: {ex.Message}");
            return false;
        }
    }

    public bool TryArmWarmup(string reason = "plugin")
    {
        var g = _game();
        if (g is null) return false;
        try
        {
            g.TryArmWarmupOrFirstPlay(reason);
            return true;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[plugins] TryArmWarmup: {ex.Message}");
            return false;
        }
    }
}

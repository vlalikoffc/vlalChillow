namespace StandChillow.LanServer.Net.Match.Modes;

/// <summary>
/// Compile-safe stub: logs once, never publishes invented C2/Time/WinTeam.
/// Bomb-family stubs share this until their own gold evidence is wired.
/// </summary>
public abstract class UnimplementedMatchModeBase : IMatchMode
{
    private bool _logged;

    public abstract string GameModeId { get; }
    public bool IsImplemented => false;

    public void Tick(IMatchModeHost host)
    {
        if (_logged) return;
        _logged = true;
        host.Log(
            $"[mode:{GameModeId}] unimplemented — refuse to drive unknown wire " +
            "(see Modes/{GameModeId}/README.md)");
    }
}

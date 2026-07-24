using StandChillow.LanServer.Net.Match.Modes;

namespace StandChillow.LanServer.Net.Match.Modes.Defuse;

/// <summary>
/// Casual Defuse — same Allies gold bomb-round flow as RankedDefuse; first-to-<b>6</b>
/// (not 8). Host combat timeout 109s. Timers via <c>DefuseFlowParams</c>.
/// Driven by <c>GameMatchHost.Allies.cs</c> Allies-family branch.
/// </summary>
public sealed class DefuseMode : IMatchMode
{
    public string GameModeId => "Defuse";
    public bool IsImplemented => true;

    public void Tick(IMatchModeHost host)
    {
        // Host poll still calls TickMatchFlow → TickAlliesFlowRoom for Allies-family C0.
    }
}

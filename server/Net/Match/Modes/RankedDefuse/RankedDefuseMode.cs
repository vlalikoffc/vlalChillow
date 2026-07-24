using StandChillow.LanServer.Net.Match.Modes;

namespace StandChillow.LanServer.Net.Match.Modes.RankedDefuse;

/// <summary>
/// RankedDefuse (Competitive / MM) — Allies gold bomb-round flow (C2=22→31→40?→101),
/// host combat timeout 109s, first-to-8. Timers via <c>DefuseFlowParams</c>.
/// Driven by <c>GameMatchHost.Allies.cs</c> Allies-family branch (not Escalation).
/// </summary>
public sealed class RankedDefuseMode : IMatchMode
{
    public string GameModeId => "RankedDefuse";
    public bool IsImplemented => true;

    public void Tick(IMatchModeHost host)
    {
        // Host poll still calls TickMatchFlow → TickAlliesFlowRoom for Allies-family C0.
    }
}

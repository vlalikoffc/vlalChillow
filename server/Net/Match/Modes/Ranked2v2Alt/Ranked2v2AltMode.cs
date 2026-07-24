using StandChillow.LanServer.Net.Match.Modes;

namespace StandChillow.LanServer.Net.Match.Modes.Ranked2v2Alt;

/// <summary>
/// Ranked2v2Alt («союзники Alt») — same Allies gold FSM as Ranked2v2; client uses the
/// other map half. Host combat timeout 109s; first-to-8. Driven by
/// <c>GameMatchHost.Allies.cs</c> via expanded Allies-family mode check.
/// </summary>
public sealed class Ranked2v2AltMode : IMatchMode
{
    public string GameModeId => "Ranked2v2Alt";
    public bool IsImplemented => true;

    public void Tick(IMatchModeHost host)
    {
        // Host poll still calls TickMatchFlow → TickAlliesFlowRoom for Allies-family C0.
    }
}

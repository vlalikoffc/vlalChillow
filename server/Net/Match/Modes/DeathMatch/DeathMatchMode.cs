namespace StandChillow.LanServer.Net.Match.Modes.DeathMatch;

/// <summary>
/// DeathMatch / TDM — implemented LAN match loop. Phase/score/end TX lives in the
/// DeathMatch / TDM — live TX in <c>GameMatchHost.DeathMatch.cs</c> (WarmUp C2=21 ~3s →
/// Live C2=30 → End C2=200/201/255), branched from <c>TickMatchFlowRoom</c> by
/// <c>C0=DeathMatch</c>. See <c>README.md</c> for the C2/score/MVP gold mapping.
/// Future: move tick ownership fully behind <see cref="IMatchMode"/>.
/// </summary>
public sealed class DeathMatchMode : IMatchMode
{
    public string GameModeId => "DeathMatch";
    public bool IsImplemented => true;

    public void Tick(IMatchModeHost host)
    {
        // Host poll still calls TickMatchFlow on GameMatchHost partials.
        // This type exists so registry / agent ownership maps have a concrete home.
    }
}

namespace StandChillow.LanServer.Net.Match.Modes.Duel;

/// <summary>
/// Duel — gold <c>MATCH_DUEL_PROBE.md</c>. Live phase/RoundEnd TX lives in
/// <c>GameMatchHost.Duel.cs</c> (branched from <c>TickMatchFlowRoom</c>).
/// </summary>
public sealed class DuelMode : IMatchMode
{
    public string GameModeId => "Duel";
    public bool IsImplemented => true;

    public void Tick(IMatchModeHost host)
    {
        // Host poll still calls TickMatchFlow on GameMatchHost partials.
    }
}

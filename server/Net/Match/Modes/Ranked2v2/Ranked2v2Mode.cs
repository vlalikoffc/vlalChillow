namespace StandChillow.LanServer.Net.Match.Modes.Ranked2v2;

/// <summary>
/// Ranked2v2 («союзники») — currently the only implemented LAN match loop.
/// Live phase/RoundEnd TX lives in <c>GameMatchHost.Ranked2v2*.cs</c> partials in this folder
/// (extracted from the old god-file; gold WinTeam path must not regress).
/// Future: move tick ownership fully behind <see cref="IMatchMode"/>.
/// </summary>
public sealed class Ranked2v2Mode : IMatchMode
{
    public string GameModeId => "Ranked2v2";
    public bool IsImplemented => true;

    public void Tick(IMatchModeHost host)
    {
        // Host poll still calls TickMatchFlow on GameMatchHost partials.
        // This type exists so registry / agent ownership maps have a concrete home.
    }
}

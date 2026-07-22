using System.Threading;
using LiteNetLib;

namespace StandChillow.LanServer.Net.Match.Host;

/// <summary>Per-LiteNetLib-peer match session state (handshake + INIT gate).</summary>
internal sealed class MatchPeerState
{
    public bool Handshaken { get; set; }
    public string? UserId { get; set; }
    public string? AppId { get; set; }
    public MatchRoom? Room { get; set; }
    public byte ActorNr { get; set; }
    public string? RosterName { get; set; }

    /// <summary>
    /// After Found: wait for joiner early identity before host managers+C2.
    /// Gate: uid + from_lobby + (avatar | ping) — see MATCH_WORLD.md.
    /// </summary>
    public bool BootstrapPending { get; set; }
    /// <summary>True after managers×8 + C2=10 were TX'd to <b>this</b> peer.</summary>
    public bool BootstrapSent { get; set; }
    public bool JoinerUidSeen { get; set; }
    public bool JoinerFromLobbySeen { get; set; }
    public bool JoinerAvatarSeen { get; set; }
    public bool JoinerPingSeen { get; set; }
    public CancellationTokenSource? BootstrapTimeoutCts { get; set; }

    public bool IsJoinerIdentityReady =>
        JoinerUidSeen && JoinerFromLobbySeen && (JoinerAvatarSeen || JoinerPingSeen);
}

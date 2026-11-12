using System.Threading;
using LiteNetLib;
using StandChillow.LanServer.Net.Match;

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
    /// <summary>
    /// Issue #28: the host already sent this peer's full identity block (uid → badgeId →
    /// verified → from_lobby → …). A rejoin that reuses the live <see cref="NetPeer"/> (same UDP
    /// session) does not repeat that batch, so the INIT gate must not wait for it again.
    /// </summary>
    /// <remarks>
    /// It must NOT make the gate pass on its own: <c>IsJoinerIdentityReady</c> still requires this
    /// join's own <c>uid</c>. Otherwise a fresh join carrying no identity would be treated as
    /// ready and get the world snapshot before the client knows which actor it is.
    /// </remarks>
    public bool IdentityAlreadyBootstrapped { get; set; }
    public CancellationTokenSource? BootstrapTimeoutCts { get; set; }

    /// <summary>
    /// Mid-match reconnect: host will re-force this fighting team after INIT (same path as
    /// inbound team SetProperty / <c>/set team</c>). Cleared after restore attempt or spectator fallback.
    /// </summary>
    public MatchTeam? PendingReconnectTeam { get; set; }

    /// <summary>
    /// Reconnect spectator fallback already ran for this peer session — never force Spectator again.
    /// </summary>
    public bool ReconnectSpectatorFallbackDone { get; set; }

    /// <summary>
    /// Ready for host bootstrap: this join's own <c>uid</c> is known, and the rest of the identity
    /// batch is either seen now or was already delivered to this peer earlier
    /// (<see cref="IdentityAlreadyBootstrapped"/> — see issue #28).
    /// </summary>
    public bool IsJoinerIdentityReady =>
        JoinerUidSeen
        && (IdentityAlreadyBootstrapped
            || (JoinerFromLobbySeen && (JoinerAvatarSeen || JoinerPingSeen)));
}

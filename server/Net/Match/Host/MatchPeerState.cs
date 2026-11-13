using System;
using System.Threading;
using LiteNetLib;
using StandChillow.LanServer.Net.Match;

namespace StandChillow.LanServer.Net.Match.Host;

/// <summary>Per-LiteNetLib-peer match session state (handshake + INIT gate).</summary>
internal sealed class MatchPeerState
{
    public bool Handshaken { get; set; }
    /// <summary>Wire uid: 2.07 auth-токен без ведущей <c>C</c> (для legacy — userId).</summary>
    public string? UserId { get; set; }
    /// <summary>
    /// Issue #39: устойчивая часть токена (<see cref="MatchIdentity.StableKeyFrom207Token"/>).
    /// Полный <see cref="UserId"/> содержит гуид сессии и меняется при каждом переподключении,
    /// поэтому «тот же игрок» и память о команде ищутся по этому ключу.
    /// </summary>
    public string? IdentityKey { get; set; }
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
    /// Issue #39: this peer took over an actor that already existed in the room — the host
    /// proved who it is by the stable token key. The reconnecting client does not re-author
    /// the identity batch, so waiting for it left the player «pre-init» for 15 s (no world,
    /// no pawn, no team) until the soft timeout.
    /// </summary>
    public bool ReconnectIdentityTrusted { get; set; }

    /// <summary>
    /// The client authored its own <c>team</c> prop after this join. A remembered reconnect
    /// team must not overwrite that fresh choice.
    /// </summary>
    public bool TeamAuthoredThisSession { get; set; }

    /// <summary>
    /// The peer's scene is up: it has sent at least one world-level packet since its bootstrap,
    /// or the hold window expired. See <see cref="MatchWorldGate"/>.
    /// </summary>
    public bool WorldReady { get; set; } = true;

    /// <summary>When <see cref="WorldReady"/> flips on even without proof (Environment.TickCount64).</summary>
    public long WorldGateUntilTick { get; set; }

    /// <summary>Close the gate for a joining peer: its world does not exist yet.</summary>
    public void HoldWorldTraffic(int holdMs)
    {
        WorldReady = false;
        WorldGateUntilTick = Environment.TickCount64 + holdMs;
    }

    /// <summary>
    /// Ready for host bootstrap: this join's own <c>uid</c> is known, and the rest of the identity
    /// batch is either seen now or was already delivered to this peer earlier
    /// (<see cref="IdentityAlreadyBootstrapped"/> — see issue #28).
    /// </summary>
    public bool IsJoinerIdentityReady =>
        ReconnectIdentityTrusted
        || (JoinerUidSeen
            && (IdentityAlreadyBootstrapped
                || (JoinerFromLobbySeen && (JoinerAvatarSeen || JoinerPingSeen))));
}

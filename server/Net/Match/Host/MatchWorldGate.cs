using StandChillow.LanServer.Net.Match;

namespace StandChillow.LanServer.Net.Match.Host;

/// <summary>
/// Per-peer «scene is not built yet» gate. Kept free of sockets so the delivery rule can be
/// regression-tested without opening ports.
/// </summary>
/// <remarks>
/// A bootstrap only <b>unlocks</b> the phone's INIT; the scene managers (<c>BombManager</c>,
/// <c>RadarManager</c>, pawns) still have to be created, which on a loading phone takes seconds.
/// While that is happening the host used to relay <c>WorldObjectState</c>/<c>WorldObjectRpc</c>
/// to the peer because <c>BootstrapSent</c> was the only «ready» bit it had. latest-2.log shows
/// the result: right after <c>TX world bootstrap trigger=reconnect-trusted joiner=3</c> the host
/// relayed a RadarManager Rpc into that socket, and the phone answered
/// «Failed to deserialize actor reference (actor with id 0 not found)» (RadarManager stack).
/// <para>
/// The gate holds world traffic until the peer itself proves the scene is alive — its first
/// <c>WorldObjectState</c>/<c>WorldObjectRpc</c>/<c>CreateWorldObject</c>/<c>DestroyWorldObject</c>
/// — and force-opens after <see cref="HoldMs"/> so a silent peer (spectator, stuck client) is
/// never starved. Room props (<c>SetProperty</c>/<c>SetProperties</c>/<c>C2</c>) and
/// <c>ReCreateSceneManager</c> are <b>not</b> gated: they are what the client builds the scene
/// from.
/// </para>
/// </remarks>
internal static class MatchWorldGate
{
    /// <summary>Upper bound on how long a peer can be held when it never answers.</summary>
    public const int HoldMs = 4000;

    /// <summary>Client opcodes that prove its scene is up and parsing world traffic.</summary>
    public static bool ProvesWorldReady(MatchOpcode opcode) =>
        opcode is MatchOpcode.WorldObjectState
            or MatchOpcode.WorldObjectRpc
            or MatchOpcode.CreateWorldObject
            or MatchOpcode.DestroyWorldObject;

    /// <summary>Hold relayed world traffic for this peer?</summary>
    public static bool HoldsTraffic(bool bootstrapSent, bool worldReady) =>
        bootstrapSent && !worldReady;

    /// <summary>Has the hold window expired (no proof arrived)?</summary>
    public static bool DeadlineExpired(long deadlineTick, long nowTick) => nowTick >= deadlineTick;
}

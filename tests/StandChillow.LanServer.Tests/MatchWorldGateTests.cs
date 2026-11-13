using StandChillow.LanServer.Net.Match;
using StandChillow.LanServer.Net.Match.Host;
using Xunit;

namespace StandChillow.LanServer.Tests;

/// <summary>
/// Regression for the reconnected phone in latest-2.log: right after
/// <c>TX world bootstrap trigger=reconnect-trusted joiner=3</c> the host relayed a RadarManager
/// Rpc into a socket whose scene was still being built, and the client threw
/// «Failed to deserialize actor reference (actor with id 0 not found)». The gate keeps world
/// traffic back until the peer itself proves the world is up.
/// </summary>
public sealed class MatchWorldGateTests
{
    [Theory]
    [InlineData(MatchOpcode.WorldObjectState, true)]
    [InlineData(MatchOpcode.WorldObjectRpc, true)]
    [InlineData(MatchOpcode.CreateWorldObject, true)]
    [InlineData(MatchOpcode.DestroyWorldObject, true)]
    [InlineData(MatchOpcode.SetProperty, false)]
    [InlineData(MatchOpcode.SetProperties, false)]
    [InlineData(MatchOpcode.ReCreateSceneManager, false)]
    [InlineData(MatchOpcode.CustomEvent, false)]
    public void Only_world_level_opcodes_prove_the_scene_is_up(MatchOpcode opcode, bool proves) =>
        Assert.Equal(proves, MatchWorldGate.ProvesWorldReady(opcode));

    [Fact]
    public void Peer_that_was_bootstrapped_but_not_proven_is_held()
    {
        Assert.True(MatchWorldGate.HoldsTraffic(bootstrapSent: true, worldReady: false));
        Assert.False(MatchWorldGate.HoldsTraffic(bootstrapSent: true, worldReady: true));
    }

    [Fact]
    public void Peer_without_bootstrap_is_not_held_it_is_simply_not_ready()
    {
        // BroadcastInitReady* skips it on the BootstrapSent check; the gate must not add a second
        // meaning to that state, otherwise the plant fan-out would silently include it.
        Assert.False(MatchWorldGate.HoldsTraffic(bootstrapSent: false, worldReady: false));
    }

    [Fact]
    public void Hold_window_expires_so_a_silent_peer_is_never_starved()
    {
        Assert.False(MatchWorldGate.DeadlineExpired(deadlineTick: 1_000, nowTick: 999));
        Assert.True(MatchWorldGate.DeadlineExpired(deadlineTick: 1_000, nowTick: 1_000));
        Assert.Equal(4000, MatchWorldGate.HoldMs);
    }
}

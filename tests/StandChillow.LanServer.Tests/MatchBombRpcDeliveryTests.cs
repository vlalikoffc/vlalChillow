using StandChillow.LanServer.Net.Match.Host;
using Xunit;

namespace StandChillow.LanServer.Tests;

/// <summary>
/// Regression: the plant reached every peer <b>twice</b> (latest-2.log round 2 — host fan-out to
/// <c>peers=3</c> followed by the exceptSender relay of the planter's original packet to
/// <c>peers=2</c>), and the phone answered the second copy with «Invalid bomber».
/// </summary>
public sealed class MatchBombRpcDeliveryTests
{
    [Fact]
    public void Planter_does_not_get_the_host_rebuilt_plant_again()
    {
        // The planter applied the plant locally before the host saw it (gaa=AllCached).
        Assert.False(MatchBombRpcDelivery.ShouldSendPlantRpc(bootstrapSent: true, isPlanter: true));
    }

    [Fact]
    public void Other_init_ready_peers_get_it_exactly_once()
    {
        Assert.True(MatchBombRpcDelivery.ShouldSendPlantRpc(bootstrapSent: true, isPlanter: false));
    }

    [Fact]
    public void Peer_that_never_got_its_bootstrap_is_not_sent_a_bare_plant()
    {
        // No world yet: the plant would be applied to a client without a BombManager.
        Assert.False(MatchBombRpcDelivery.ShouldSendPlantRpc(bootstrapSent: false, isPlanter: false));
    }

    [Fact]
    public void Accepted_allies_plant_is_not_relayed_a_second_time()
    {
        Assert.True(MatchBombRpcDelivery.SuppressOriginalRelay(alliesFamily: true, plantAccepted: true));
    }

    [Fact]
    public void Classic_bomb_modes_keep_the_except_sender_relay()
    {
        // No host fan-out there, so the relay is the only delivery to the other peers.
        Assert.False(MatchBombRpcDelivery.SuppressOriginalRelay(alliesFamily: false, plantAccepted: true));
        Assert.False(MatchBombRpcDelivery.SuppressOriginalRelay(alliesFamily: true, plantAccepted: false));
    }
}

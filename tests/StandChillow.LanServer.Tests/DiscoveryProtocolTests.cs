using System.Text;
using StandChillow.LanServer.Lan;
using Xunit;

namespace StandChillow.LanServer.Tests;

public sealed class DiscoveryProtocolTests
{
    [Fact]
    public void Discovery_RecognizesTheObserved207BonjourV3Probe()
    {
        var probe = Encoding.ASCII.GetBytes("SC LAN Bonjour V3");

        Assert.Equal(probe, LanDiscoveryHost.ProbeMagicBonjourV3);
        Assert.Equal(probe, LanDiscoveryHost.ProbeMagic);
        Assert.Equal("bonjour-v3", LanDiscoveryHost.MatchProbe(probe));
    }

    [Theory]
    [InlineData("StandChillow LAN Bonjour V2", "bonjour-v2")]
    [InlineData("sosalbolt482", "sosalbolt482")]
    public void Discovery_KeepsRecognizingEarlierProbeFormats(string probe, string expectedKind)
    {
        Assert.Equal(expectedKind, LanDiscoveryHost.MatchProbe(Encoding.ASCII.GetBytes(probe)));
    }

    [Fact]
    public void Discovery_DoesNotTreatArbitraryTrafficAsAProbe()
    {
        Assert.Null(LanDiscoveryHost.MatchProbe("SC LAN Bonjour V2"u8));
    }
}

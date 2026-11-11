using StandChillow.LanServer.Net.Lobby;
using StandChillow.LanServer.Net.Match;
using Xunit;

namespace StandChillow.LanServer.Tests;

public sealed class Match207HandshakeTests
{
    [Fact]
    public void ExtendedHandshake207_DecodesTheLiveRequestLayout()
    {
        const string token = "CLB143C7D41FAE30EB14F49B1678E3933C9_dac2313f130647fcb10c980fe6136cdc";
        var packet = MatchCodec.BuildHandshake207Request(MatchAuth.AppId, "", token);
        Assert.Equal(100, packet.Length);
        Assert.True(MatchCodec.TryOpenBody(
            packet, out _, out var opcode, out _, out var body));
        Assert.Equal(MatchOpcode.FetchServerTimeRequest, opcode);

        var reader = new LobbyReader(body);
        var request = MatchCodec.ParseHandshake207RequestBody(reader);

        Assert.Equal(MatchAuth.ProtocolVersion207, request.ProtocolVersion);
        Assert.Equal(MatchAuth.AppId, request.AppId);
        Assert.Equal("", request.UserId);
        Assert.Equal(token, request.AuthToken);
        Assert.Equal(0, reader.Remaining);
    }

    [Fact]
    public void Handshake207Response_MatchesThePhoneHostWireLayout()
    {
        var response = MatchCodec.BuildHandshake207Response(HandshakeResult.Success, 1_140_666);

        Assert.Equal(
            new byte[] { 4, 1, 0xBA, 0x67, 0x11, 0, 0, 0, 0, 0, 4 },
            response);
        Assert.True(MatchCodec.TryOpenBody(
            response, out _, out var opcode, out var serverTime, out var body));
        Assert.Equal(MatchOpcode.HandshakeResponse, opcode);
        Assert.Equal(1_140_666, serverTime);
        Assert.Equal(
            HandshakeResult.Success,
            MatchCodec.ParseHandshakeResponseBody(new LobbyReader(body)));
    }
}

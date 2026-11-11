using StandChillow.LanServer.Net.Lobby;
using StandChillow.LanServer.Net.Match;
using Xunit;

namespace StandChillow.LanServer.Tests;

public sealed class Match207JoinRoomTests
{
    [Fact]
    public void FoundResponse_MatchesThe207ActorIdentityAndGapLayout()
    {
        (byte Nr, string Name, string UserId)[] expectedActors =
        [
            (1, "player_6472", "LB143C7D41FAE30EB14F49B1678E3933C9"),
            (2, "FinalSapper78", "LB4B7BA14444774A9D4FBBEC23F4519998_425b3cb5f43a4b678668831543224948"),
            (3, "probe", "LB115248D58295549502339697C4CCF9DC_b21ef71e6c3345729dbb21897b024f44"),
        ];
        var gap = new MatchGapRoom
        {
            RoomName = "Dedik",
            Open = true,
            MaxActorsHint = MatchGapDefaults.MaxActorsHint,
            RoomProps = MatchGapDefaults.BuildRoomProps("Ranked2v2", "Prison 2x2"),
            Actors = expectedActors.Select(a => new MatchGapActor
            {
                ActorNr = a.Nr,
                Name = a.Name,
                UserId = a.UserId,
                Flag = false,
            }).ToList(),
            TrailingByte = MatchGapDefaults.TrailingByte,
        };

        var response = MatchCodec.BuildJoinRoomResponse(
            JoinRoomResult.Found, 1524, actorNr: 3, debugMessage: "Dedik", gap: gap);

        Assert.Equal(341, response.Length);
        byte[] authoritativePhoneResponse = Convert.FromHexString(
            "040CF40500000000000000000105446564696B01030105446564696B01140107000243300A0D0952616E6B6564327632" +
            "0243310A0D0A507269736F6E20327832024332FF00024333FF0206726567696F6E0A0D056C6F63616C0967616D655F74" +
            "797065FF020A73657373696F6E5F6964010000000003010B706C617965725F36343732224C4231343343374434314641" +
            "45333045423134463439423136373845333933334339010A000000020D46696E616C5361707065723738434C42344237" +
            "42413134343434373734413944344642424543323346343531393939385F343235623363623566343361346236373836" +
            "3638383331353433323234393438010A000000030570726F6265434C4231313532343844353832393535343935303233" +
            "333936393743344343463944435F6232316566373165366333333435373239646262323138393762303234663434010A" +
            "0000000100");
        Assert.Equal(authoritativePhoneResponse, response);
        Assert.True(MatchCodec.TryOpenBody(
            response, out _, out var opcode, out _, out var body));
        Assert.Equal(MatchOpcode.JoinRoomResponse, opcode);

        var r = new LobbyReader(body);
        var header = MatchCodec.ParseJoinRoomResponseBody(r);
        Assert.Equal(JoinRoomResult.Found, header.Result);
        Assert.Equal((byte)3, header.ActorNr);
        Assert.True(header.HasRoomProperties);
        Assert.Equal("Dedik", r.ReadString());
        Assert.True(r.ReadBool());
        Assert.Equal(20, r.ReadByte());
        Assert.True(r.ReadBool());
        Assert.Equal(7, r.ReadBareProps().Count);
        Assert.Equal(3, r.ReadByte());

        foreach (var expected in expectedActors)
        {
            Assert.Equal(expected.Nr, r.ReadByte());
            Assert.Equal(expected.Name, r.ReadString());
            Assert.Equal(expected.UserId, r.ReadString());
            Assert.True(r.ReadBool());
            Assert.Empty(r.ReadTypedProps());
            Assert.False(r.ReadBool());
        }

        Assert.Equal(1, r.ReadByte());
        Assert.False(r.ReadBool());
        Assert.Equal(0, r.Remaining);
    }
}

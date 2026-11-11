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
            (2, "FinalSapper78", "CLB4B7BA14444774A9D4FBBEC23F4519998_425b3cb5f43a4b678668831543224948"),
            (3, "probe", "CLB115248D58295549502339697C4CCF9DC_b21ef71e6c3345729dbb21897b024f44"),
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

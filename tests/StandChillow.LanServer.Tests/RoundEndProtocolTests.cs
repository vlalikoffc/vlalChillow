using StandChillow.LanServer.Net;
using StandChillow.LanServer.Net.Lobby;
using StandChillow.LanServer.Net.Match;
using Xunit;

namespace StandChillow.LanServer.Tests;

public sealed class RoundEndProtocolTests
{
    [Fact]
    public void AlliesRoundEnd_EncodesWinTeamAndC2InTheSamePacket()
    {
        var props = GameMatchHost.BuildAlliesRoundEndRoomProps(
            nowSec: 123.5,
            winner: MatchTeam.Tr,
            scoreTr: 4,
            scoreCt: 2,
            coLossesTr: 0,
            coLossesCt: 1,
            GameMatchHost.BuildWinTeamProps(MatchTeam.Tr, mvpNr: 7, mvpCode: 3));

        var decoded = DecodeSingleSetProperties(props);

        Assert.Equal(0, decoded.ActorNr);
        Assert.Equal(0, decoded.Reader.Remaining);
        AssertC2AndWinTeam(decoded.Props, MatchTeam.Tr);
        Assert.Equal(4, RequiredProp(decoded.Props, MatchRoomPropKeys.TrScore).Int);
    }

    [Fact]
    public void GenericRoundEnd_EncodesWinTeamAndC2InTheSamePacket()
    {
        var props = GameMatchHost.BuildRoundEndRoomProps(
            nowSec: 50,
            winner: MatchTeam.Ct,
            scoreTr: 1,
            scoreCt: 3,
            coLossesTr: 2,
            coLossesCt: 0,
            GameMatchHost.BuildWinTeamProps(MatchTeam.Ct, mvpNr: 9, mvpCode: 1));

        var decoded = DecodeSingleSetProperties(props);

        AssertC2AndWinTeam(decoded.Props, MatchTeam.Ct);
        Assert.Equal(3, RequiredProp(decoded.Props, MatchRoomPropKeys.CtScore).Int);
    }

    [Fact]
    public void DuelRoundEnd_EncodesWinTeamAndC2InTheSamePacket()
    {
        var props = GameMatchHost.BuildDuelRoundEndRoomProps(
            nowSec: 75,
            winner: MatchTeam.Tr,
            scoreTr: 8,
            scoreCt: 6,
            GameMatchHost.BuildWinTeamProps(MatchTeam.Tr, mvpNr: 2, mvpCode: 4));

        var decoded = DecodeSingleSetProperties(props);

        AssertC2AndWinTeam(decoded.Props, MatchTeam.Tr);
        Assert.Equal(8, RequiredProp(decoded.Props, MatchRoomPropKeys.TrScore).Int);
    }

    private static (byte ActorNr, List<(string Key, LobbyVariant Value)> Props, LobbyReader Reader)
        DecodeSingleSetProperties(IReadOnlyList<(string Key, LobbyVariant Value)> props)
    {
        var packet = MatchCodec.BuildSetProperties(serverTime: 1234, actorNr: 0, props);
        Assert.True(MatchCodec.TryOpenBody(
            packet, out _, out var opcode, out _, out var body));
        Assert.Equal(MatchOpcode.SetProperties, opcode);

        var reader = new LobbyReader(body);
        var decoded = MatchCodec.ParseSetPropertiesBody(reader);
        return (decoded.ActorNr, decoded.Props, reader);
    }

    private static void AssertC2AndWinTeam(
        IReadOnlyList<(string Key, LobbyVariant Value)> props,
        MatchTeam expectedWinner)
    {
        var c2 = RequiredProp(props, MatchRoomPropKeys.C2);
        Assert.Equal(LobbyVariantKind.Byte, c2.Kind);
        Assert.Equal(MatchC2States.MatchStarted, c2.Byte);

        var winTeam = RequiredProp(props, MatchRoomPropKeys.WinTeam);
        Assert.Equal(LobbyVariantKind.Properties, winTeam.Kind);
        var nested = Assert.IsAssignableFrom<IReadOnlyList<(string Key, LobbyVariant Value)>>(
            winTeam.Props);
        var team = RequiredProp(nested, MatchWinTeamKeys.Team);
        Assert.Equal(LobbyVariantKind.Byte, team.Kind);
        Assert.Equal((byte)expectedWinner, team.Byte);
    }

    private static LobbyVariant RequiredProp(
        IReadOnlyList<(string Key, LobbyVariant Value)> props,
        string key)
    {
        var matches = props.Where(p => p.Key == key).ToArray();
        Assert.Single(matches);
        return matches[0].Value;
    }
}

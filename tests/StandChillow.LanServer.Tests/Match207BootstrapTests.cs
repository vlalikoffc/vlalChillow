using StandChillow.LanServer.Net.Lobby;
using StandChillow.LanServer.Net.Match;
using Xunit;

namespace StandChillow.LanServer.Tests;

public sealed class Match207BootstrapTests
{
    [Fact]
    public void SetInternalProperty_MatchesPhoneIncludingReservedInt32()
    {
        var packet = MatchCodec.BuildSetInternalProperty(
            9827, actorNr: 1, MatchCodec.InternalPropNick, LobbyVariant.FromString("влал"));

        Assert.Equal(
            Convert.FromHexString("0466632600000000000001FF0A0D08D0B2D0BBD0B0D0BB"),
            packet);

        var r = Open(packet, MatchOpcode.SetInternalProperty);
        Assert.Equal(1, r.ReadByte());
        Assert.Equal(MatchCodec.InternalPropNick, r.ReadByte());
        Assert.Equal("влал", r.ReadFzp().String);
        Assert.Equal(0, r.Remaining);
    }

    [Fact]
    public void SetProperty_MatchesPhoneAndDecodesPastReservedInt32()
    {
        var packet = MatchCodec.BuildSetProperty(
            9828, actorNr: 1, "uid", LobbyVariant.FromString("Offline"));

        Assert.Equal(
            Convert.FromHexString("0465642600000000000001037569640A0D074F66666C696E65"),
            packet);

        var r = Open(packet, MatchOpcode.SetProperty);
        var decoded = MatchCodec.ParseSetPropertyBody(r);
        Assert.Equal(1, decoded.ActorNr);
        Assert.Equal("uid", decoded.Key);
        Assert.Equal("Offline", decoded.Value.String);
        Assert.Equal(0, r.Remaining);
    }

    [Fact]
    public void SceneManagerCreate_MatchesPhoneAndDecodesPastReservedInt32()
    {
        var packet = MatchCodec.BuildCreateWorldObject(
            9829, WorldObjectKind.SceneManager, objectId: 1, "GameRpcHelper", fusTag: 0xF8);

        Assert.Equal(
            Convert.FromHexString("04C86526000000000000FF0001000D47616D6552706348656C706572F8"),
            packet);

        var r = Open(packet, MatchOpcode.CreateWorldObject);
        var decoded = MatchCodec.ParseCreateWorldObjectBody(r);
        Assert.Equal(WorldObjectKind.SceneManager, decoded.Kind);
        Assert.Equal(1, decoded.ObjectId);
        Assert.Equal("GameRpcHelper", decoded.TypeName);
        Assert.Equal(0xF8, decoded.FusTag);
        Assert.Empty(decoded.Trailing);
        Assert.Equal(0, r.Remaining);
    }

    [Fact]
    public void C2WaitingUnlock_MatchesPhoneAndDecodesPastReservedInt32()
    {
        var packet = MatchCodec.BuildSetProperties(
            10057, actorNr: 0,
            [(MatchRoomPropKeys.C2, LobbyVariant.FromByte(MatchHostActor.C2AfterManagers))]);

        Assert.Equal(
            Convert.FromHexString("04644927000000000000000100024332FF0A"),
            packet);

        var r = Open(packet, MatchOpcode.SetProperties);
        var decoded = MatchCodec.ParseSetPropertiesBody(r);
        Assert.Equal(0, decoded.ActorNr);
        var (key, value) = Assert.Single(decoded.Props);
        Assert.Equal(MatchRoomPropKeys.C2, key);
        Assert.Equal(MatchHostActor.C2AfterManagers, value.Byte);
        Assert.Equal(0, r.Remaining);
    }

    [Fact]
    public void WorldObjectRpc_MatchesPhoneAndDecodesPastReservedInt32()
    {
        var packet = MatchCodec.BuildWorldObjectRpc(
            11566, objectId: 129, rpcId: 1, gaaTarget: 4, field: 2,
            timeValue: 11.561, MatchCodec.BuildPawnRpcSpawnTailPayload());

        Assert.Equal(
            Convert.FromHexString(
                "04CB2E2D000000000000810001040200AC1C5A643B1F2740" +
                "6400000000000000000100004040"),
            packet);

        var r = Open(packet, MatchOpcode.WorldObjectRpc);
        var decoded = MatchCodec.ParseWorldObjectRpcBody(r);
        Assert.Equal(129, decoded.ObjectId);
        Assert.Equal(1, decoded.RpcId);
        Assert.Equal(4, decoded.GaaTarget);
        Assert.Equal(2, decoded.Field);
        Assert.Equal(11.561, decoded.TimeValue);
        Assert.Equal(14, decoded.Payload.Length);
        Assert.Equal(0, r.Remaining);
    }

    [Fact]
    public void SceneTransitions_MatchPhoneReservedInt32()
    {
        Assert.Equal(
            Convert.FromHexString("04CA86750000000000000400"),
            MatchCodec.BuildReCreateSceneManager(30086, objectId: 4));
        Assert.Equal(
            Convert.FromHexString("04C986750000000000008100"),
            MatchCodec.BuildDestroyWorldObject(30086, objectId: 129));
    }

    private static LobbyReader Open(byte[] packet, MatchOpcode expectedOpcode)
    {
        Assert.True(MatchCodec.TryOpenBody(
            packet, out _, out var opcode, out _, out var body));
        Assert.Equal(expectedOpcode, opcode);
        return new LobbyReader(body);
    }
}

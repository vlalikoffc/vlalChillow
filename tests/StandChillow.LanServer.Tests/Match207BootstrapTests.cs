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

    /// <summary>
    /// Phone host sends a bare <c>00 0F</c> right after Found
    /// (gold <c>20261111_074252_005_match_rx003_op15_len2.bin</c>); the dedicated host must too.
    /// </summary>
    [Fact]
    public void RoomInitReady_MatchesPhoneEmptyNudge()
    {
        var packet = MatchCodec.BuildRoomInitReady();

        Assert.Equal(new byte[] { 0x00, 0x0F }, packet);
        Assert.True(MatchCodec.TryParseEnvelope(
            packet, out var flags, out var opcode, out _, out var bodyOffset));
        Assert.Equal(MatchFrameFlags.None, flags);
        Assert.Equal(MatchOpcode.RoomInitReady, opcode);
        Assert.Equal((byte)15, (byte)opcode);
        Assert.Equal(packet.Length, bodyOffset);
    }

    /// <summary>
    /// Phone host identity block: uid → badgeId → verified → from_lobby → avatar → ping
    /// (gold <c>20261111_074258_049..053</c>). The dedicated host must not skip <c>verified</c>.
    /// </summary>
    [Fact]
    public void HostIdentity_IncludesVerifiedBetweenBadgeIdAndFromLobby()
    {
        var verified = MatchCodec.BuildSetProperty(
            9829, actorNr: 1, MatchRoomPropKeys.Verified, LobbyVariant.FromBool(false));

        Assert.Equal(
            Convert.FromHexString("04656526000000000000010876657269666965640600"),
            verified);
    }

    /// <summary>
    /// WeaponDropManager / GrenadeManager catalogs are exactly the gold 51 ids
    /// (gold len=88/85 CreateWorldObject). 89 (Hands) and 101 must stay out.
    /// </summary>
    [Fact]
    public void DropCatalog_MatchesGoldPhoneManagerPayload()
    {
        Assert.Equal(51, MatchSceneManagers.DropCatalogIds.Length);
        Assert.DoesNotContain((byte)89, MatchSceneManagers.DropCatalogIds);
        Assert.DoesNotContain((byte)101, MatchSceneManagers.DropCatalogIds);
        Assert.Equal(102, MatchSceneManagers.DropCatalogIds[^1]);

        var payload = MatchCodec.BuildDropCatalogPayload(MatchSceneManagers.DropCatalogIds);
        var cwo = MatchCodec.BuildCreateWorldObject(
            9829, WorldObjectKind.SceneManager, objectId: 4, "WeaponDropManager",
            fusTag: 0xFC, ownerActorNr: null, trailingPayload: payload);

        Assert.Equal(88, cwo.Length);
        Assert.Equal(51, System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(
            payload.AsSpan(0, 4)));
    }

    private static LobbyReader Open(byte[] packet, MatchOpcode expectedOpcode)
    {
        Assert.True(MatchCodec.TryOpenBody(
            packet, out _, out var opcode, out _, out var body));
        Assert.Equal(expectedOpcode, opcode);
        return new LobbyReader(body);
    }
}

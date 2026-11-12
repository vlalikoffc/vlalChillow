using StandChillow.LanServer.Net;
using StandChillow.LanServer.Net.Lobby;
using StandChillow.LanServer.Net.Match;
using StandChillow.LanServer.Net.Match.Host;
using Xunit;

namespace StandChillow.LanServer.Tests;

/// <summary>
/// Issue #28 — "data is not sent after rejoining": a peer that comes back mid-match got no state
/// for one to two rounds. Two things had to hold:
/// <list type="bullet">
/// <item>the tracked round entities must keep every decoded <c>CreateWorldObject</c> field, so a
/// rejoin can rebuild the same object for the returning peer,</item>
/// <item>the INIT identity gate must not wait again for an identity batch a rejoining peer will
/// never resend.</item>
/// </list>
/// </summary>
public sealed class ReconnectSnapshotTests
{
    [Fact]
    public void TrackedRoundEntity_KeepsEveryFieldTheClientNeeds()
    {
        var room = NewRoom();
        var packet = MatchCodec.BuildCreateWorldObject(
            serverTime: 1234,
            kind: WorldObjectKind.Entity,
            objectId: 77,
            typeName: "WeaponDrop",
            fusTag: 101,
            ownerActorNr: 4,
            trailingPayload: [0x01, 0x02]);

        Assert.True(MatchCodec.TryOpenBody(
            packet, out _, out var opcode, out _, out var body));
        Assert.Equal(MatchOpcode.CreateWorldObject, opcode);
        var parsed = MatchCodec.ParseCreateWorldObjectBody(new LobbyReader(body));
        room.TrackedRoundEntities[77] = new TrackedRoundEntity(parsed);

        var stored = room.TrackedRoundEntities[77];
        Assert.Equal("WeaponDrop", stored.TypeName);
        Assert.Equal((short)77, stored.Fields.ObjectId);
        Assert.Equal("WeaponDrop", stored.Fields.TypeName);
        Assert.Equal((byte?)4, stored.Fields.OwnerActorNr);
        // Every field the client needs must survive tracking — a rebuilt envelope that dropped
        // owner or trailing payload would show peers a different object than the original.
        Assert.Equal((byte)101, stored.Fields.FusTag);
        Assert.Equal(new byte[] { 0x01, 0x02 }, stored.Fields.Trailing);
    }

    [Fact]
    public void ReplayedEntityReproducesTheOriginalFields()
    {
        var packet = MatchCodec.BuildCreateWorldObject(
            serverTime: 5000,
            kind: WorldObjectKind.Entity,
            objectId: 88,
            typeName: "Grenade",
            fusTag: 77,
            ownerActorNr: 3,
            trailingPayload: [0x0A, 0x0B, 0x0C]);

        Assert.True(MatchCodec.TryOpenBody(packet, out _, out _, out _, out var body));
        var stored = new TrackedRoundEntity(MatchCodec.ParseCreateWorldObjectBody(new LobbyReader(body)));

        // Same builder path the reconnect snapshot uses — only the host header time is fresh.
        var replay = MatchCodec.BuildCreateWorldObject(
            serverTime: 9999,
            kind: stored.Fields.Kind,
            objectId: stored.Fields.ObjectId,
            typeName: stored.Fields.TypeName,
            fusTag: stored.Fields.FusTag,
            ownerActorNr: stored.Fields.OwnerActorNr,
            trailingPayload: stored.Fields.Trailing);

        Assert.True(MatchCodec.TryOpenBody(replay, out _, out _, out var replayTime, out var replayBody));
        Assert.Equal((int?)9999, replayTime);
        Assert.Equal(body, replayBody);
    }

    /// <summary>
    /// Issue #28 — the latch only skips the *repeat* of the identity batch. It must never make the
    /// gate pass on its own, otherwise a rejoin that carries no identity would receive the live
    /// world before the client knows which actor it is.
    /// </summary>
    [Fact]
    public void RejoiningPeerSkipsOnlyTheRepeatOfTheIdentityBatch()
    {
        var st = new MatchPeerState();
        Assert.False(st.IsJoinerIdentityReady);

        st.IdentityAlreadyBootstrapped = true;
        Assert.False(st.IsJoinerIdentityReady); // still needs this join's uid

        st.JoinerUidSeen = true;
        Assert.True(st.IsJoinerIdentityReady);  // from_lobby/avatar need not repeat
    }

    [Fact]
    public void FreshPeerStillNeedsTheFullIdentityBatch()
    {
        var st = new MatchPeerState { JoinerUidSeen = true };
        Assert.False(st.IsJoinerIdentityReady);

        st.JoinerFromLobbySeen = true;
        Assert.False(st.IsJoinerIdentityReady); // still needs avatar or ping

        st.JoinerAvatarSeen = true;
        Assert.True(st.IsJoinerIdentityReady);
    }

    private static MatchRoom NewRoom() => new()
    {
        PasswordKey = "test",
        RoomId = "Sandstone",
    };
}

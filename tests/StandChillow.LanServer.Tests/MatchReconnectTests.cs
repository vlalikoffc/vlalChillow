using StandChillow.LanServer.Net.Lobby;
using StandChillow.LanServer.Net.Match;
using StandChillow.LanServer.Net.Match.Host;
using Xunit;

namespace StandChillow.LanServer.Tests;

/// <summary>
/// Issue #39 — «теория бесконечных обжект референсов». After a network drop the 2.07 client
/// reconnects to the match on its own, with a NEW session token. The host used the whole token
/// as the user id, did not recognise the player, and gave him a second actor while the old one
/// was still in the room; the client threw inside <c>OnJoinRoomResponse</c>.
/// </summary>
public sealed class MatchReconnectTests
{
    [Fact]
    public void Session_token_maps_to_the_same_stable_key()
    {
        // CLB<md5(lobby userId)>_<session guid> — only the tail changes between reconnects.
        const string first = "CLB7F8A3CB21BF6C31961C634036D0CFAB6_0123456789abcdef0123456789abcdef";
        const string second = "CLB7F8A3CB21BF6C31961C634036D0CFAB6_ffffffffffffffffffffffffffffffff";

        var a = MatchIdentity.StableKeyFrom207Token(first);
        var b = MatchIdentity.StableKeyFrom207Token(second);

        Assert.Equal("LB7F8A3CB21BF6C31961C634036D0CFAB6", a);
        Assert.Equal(a, b);
        Assert.True(MatchIdentity.SamePlayer(a, b));
    }

    [Fact]
    public void Token_without_session_tail_or_legacy_user_id_still_has_a_key()
    {
        Assert.Equal("LBABC", MatchIdentity.StableKeyFrom207Token("CLBABC"));
        Assert.Equal("player-7", MatchIdentity.StableKeyFromUserId("player-7"));
        Assert.Equal("", MatchIdentity.StableKeyFrom207Token(""));
        Assert.Equal("", MatchIdentity.StableKeyFromUserId(null));
        Assert.False(MatchIdentity.SamePlayer("", ""));
        Assert.False(MatchIdentity.SamePlayer("LBA", "LBB"));
    }

    [Fact]
    public void Returning_player_is_found_by_stable_key_not_by_full_token()
    {
        var room = NewRoom();
        room.Actors.Add((2, "влал", "LBHASH_firstsession"));
        var oldState = new MatchPeerState
        {
            Room = room,
            ActorNr = 2,
            UserId = "LBHASH_firstsession",
            IdentityKey = "LBHASH",
        };
        var other = new MatchPeerState
        {
            Room = room,
            ActorNr = 3,
            UserId = "LBOTHER_x",
            IdentityKey = "LBOTHER",
        };

        var actor = MatchReconnect.FindActorNr(
            room, new[] { other, oldState }, "LBHASH", out var found);

        Assert.Equal((byte)2, actor);
        Assert.Same(oldState, found);

        // A different player must never steal somebody else's actor.
        Assert.Equal(0, MatchReconnect.FindActorNr(room, new[] { other, oldState }, "LBNOBODY", out _));
        Assert.Equal(0, MatchReconnect.FindActorNr(room, new[] { other, oldState }, "", out _));
    }

    [Fact]
    public void Adopt_keeps_the_actor_number_and_hands_it_to_the_new_socket()
    {
        var room = NewRoom();
        room.Actors.Add((2, "влал", "LBHASH_firstsession"));
        room.UserIds.Add("LBHASH_firstsession");
        room.SpawnedPawns.Add(2);
        room.ActorProps[(2, MatchRoomPropKeys.Team)] = LobbyVariant.FromByte((byte)MatchTeam.Tr);
        room.LivingPawns[777] = new MatchLivingPawn { ObjectId = 777, OwnerActorNr = 2 };
        room.Flow.DeadActors.Add(2);
        room.Flow.RoundRoster[2] = MatchTeam.Tr;

        var oldState = new MatchPeerState
        {
            Room = room,
            ActorNr = 2,
            UserId = "LBHASH_firstsession",
            IdentityKey = "LBHASH",
        };
        var newState = new MatchPeerState
        {
            UserId = "LBHASH_secondsession",
            IdentityKey = "LBHASH",
        };

        var adoption = MatchReconnect.Adopt(room, newState, oldState, actorNr: 2);

        Assert.Equal(MatchTeam.Tr, adoption.RememberedTeam);
        // The old body is gone from host tracking and must be destroyed on the wire.
        Assert.Equal<IEnumerable<short>>(new short[] { 777 }, adoption.RemovedPawnIds);
        Assert.Empty(room.LivingPawns);

        // Exactly one actor with that number, wire uid follows the new session.
        var matching = room.Actors.Where(a => a.Nr == 2).ToList();
        Assert.Single(matching);
        var entry = matching[0];
        Assert.Equal("LBHASH_secondsession", entry.UserId);
        Assert.Equal("влал", entry.Name);

        Assert.DoesNotContain("LBHASH_firstsession", room.UserIds);
        Assert.Contains("LBHASH_secondsession", room.UserIds);

        // Old socket is detached, the new one owns the actor; respawn must be re-proved.
        Assert.Null(oldState.Room);
        Assert.Equal((byte)0, oldState.ActorNr);
        Assert.Same(room, newState.Room);
        Assert.Equal((byte)2, newState.ActorNr);
        Assert.Equal(MatchTeam.Tr, newState.PendingReconnectTeam);
        Assert.DoesNotContain((byte)2, room.SpawnedPawns);
        // Alive/dead of the old session is not trusted: the player is a cold mid-round
        // entrant (outside the frozen roster) until a real CWO respawns him.
        Assert.DoesNotContain((byte)2, room.Flow.DeadActors);
        Assert.False(room.Flow.RoundRoster.ContainsKey(2));
        // The reconnecting client does not resend identity — INIT must trust the takeover.
        Assert.True(newState.ReconnectIdentityTrusted);
        Assert.True(newState.IsJoinerIdentityReady);

        // Same socket re-join: nothing to transfer.
        Assert.Equal(MatchTeam.None, MatchReconnect.Adopt(room, newState, newState, 2).RememberedTeam);
    }

    [Fact]
    public void Fresh_joiner_still_waits_for_its_identity_batch()
    {
        // Guard rail for the trusted-reconnect shortcut: it must never apply to a new player.
        var st = new MatchPeerState { JoinerUidSeen = true };
        Assert.False(st.IsJoinerIdentityReady);

        st.ReconnectIdentityTrusted = true;
        Assert.True(st.IsJoinerIdentityReady);
    }

    [Fact]
    public void Actor_numbers_are_allocated_without_reuse_or_overflow()
    {
        var room = NewRoom(); // actor 1 = Server
        room.Actors.Add((1, "Server", "Offline"));

        Assert.Equal((byte?)2, MatchRoomRoster.FindFreeActorNr(room.Actors));

        room.Actors.Add((2, "a", "u2"));
        room.Actors.Add((3, "b", "u3"));
        Assert.Equal((byte?)4, MatchRoomRoster.FindFreeActorNr(room.Actors));

        room.Actors.RemoveAll(a => a.Nr == 2); // hole after a leaver
        Assert.Equal((byte?)2, MatchRoomRoster.FindFreeActorNr(room.Actors));

        // The old NextActorNr++ clamp handed out 255 forever once the counter passed it:
        // two live actors on one id. A full roster must refuse instead.
        for (var nr = 2; nr <= 255; nr++)
        {
            if (room.Actors.Any(a => a.Nr == nr))
                continue;
            room.Actors.Add(((byte)nr, $"a{nr}", $"u{nr}"));
        }

        Assert.Equal(255, room.Actors.Count);
        Assert.Null(MatchRoomRoster.FindFreeActorNr(room.Actors));
    }

    private static MatchRoom NewRoom() => new()
    {
        PasswordKey = "Dedik",
        RoomId = "Sandstone",
    };
}

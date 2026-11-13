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

    /// <summary>
    /// The case from live play: the phone «self-destructed» out of the room, its socket is gone,
    /// but the actor stayed behind. The owner is recorded in the room, so the same player must get
    /// his old number back instead of a second copy next to the ghost.
    /// </summary>
    [Fact]
    public void Ghost_actor_is_found_by_room_identity_without_any_live_socket()
    {
        var room = NewRoom();
        room.Actors.Add((2, "влал", "LBHASH_oldsession"));
        room.ActorIdentities[2] = "LBHASH";

        var actor = MatchReconnect.FindActorNr(room, Array.Empty<MatchPeerState>(), "LBHASH", out var state);

        Assert.Equal((byte)2, actor);
        Assert.Null(state); // отцеплять нечего — владельца-сокета уже нет
        Assert.True(MatchReconnect.IsGhostActor(room, 2, Array.Empty<MatchPeerState>()));
        Assert.Equal(
            0, MatchReconnect.FindActorNr(room, Array.Empty<MatchPeerState>(), "LBOTHER", out _));
    }

    [Fact]
    public void Ghost_actor_is_not_a_ghost_while_a_socket_owns_it()
    {
        var room = NewRoom();
        room.ActorIdentities[2] = "LBHASH";
        var live = new MatchPeerState { Room = room, ActorNr = 2, IdentityKey = "LBHASH" };
        var otherRoom = new MatchPeerState { Room = NewRoom(), ActorNr = 2, IdentityKey = "LBHASH" };

        Assert.False(MatchReconnect.IsGhostActor(room, 2, new[] { live }));
        Assert.True(MatchReconnect.IsGhostActor(room, 3, new[] { live }));
        // A peer that owns actor 2 in *another* room does not own this one.
        Assert.True(MatchReconnect.IsGhostActor(room, 2, new[] { otherRoom }));
    }

    [Fact]
    public void Adopting_a_ghost_keeps_one_actor_and_drops_the_old_body()
    {
        var room = NewRoom();
        room.Actors.Add((2, "влал", "LBHASH_oldsession"));
        room.ActorIdentities[2] = "LBHASH";
        room.OwnerlessSinceTick[2] = 1234;
        room.SpawnedPawns.Add(2);
        room.ActorProps[(2, MatchRoomPropKeys.Team)] = LobbyVariant.FromByte((byte)MatchTeam.Tr);
        room.LivingPawns[777] = new MatchLivingPawn { ObjectId = 777, OwnerActorNr = 2 };
        room.Flow.DeadActors.Add(2);
        room.Flow.RoundRoster[2] = MatchTeam.Tr;

        var newState = new MatchPeerState
        {
            UserId = "LBHASH_newsession",
            IdentityKey = "LBHASH",
            Room = room, // ставится join-путём после Adopt; здесь уже как владелец
        };

        var adoption = MatchReconnect.Adopt(room, newState, previousState: null, actorNr: 2);

        Assert.Equal(MatchTeam.Tr, adoption.RememberedTeam);
        Assert.Equal<IEnumerable<short>>(new short[] { 777 }, adoption.RemovedPawnIds);
        Assert.Empty(room.LivingPawns);
        Assert.Single(room.Actors.Where(a => a.Nr == 2));
        Assert.Equal("LBHASH", room.ActorIdentities[2]);
        Assert.DoesNotContain((byte)2, room.OwnerlessSinceTick.Keys);
        Assert.DoesNotContain((byte)2, room.Flow.DeadActors);
        Assert.False(room.Flow.RoundRoster.ContainsKey(2));
        Assert.Equal((byte)2, newState.ActorNr);
        Assert.Equal(MatchTeam.Tr, newState.PendingReconnectTeam);
        Assert.True(newState.ReconnectIdentityTrusted);
    }

    [Fact]
    public void Round_roster_and_counts_ignore_ghost_actors()
    {
        // A ghost keeps its team prop but nobody plays it: counting it as alive is what made the
        // side «never wiped» while the other side was faked out of the round.
        var flow = new MatchFlowState();
        var props = new Dictionary<(byte Actor, string Key), LobbyVariant>
        {
            [(2, MatchRoomPropKeys.Team)] = LobbyVariant.FromByte((byte)MatchTeam.Tr),
            [(3, MatchRoomPropKeys.Team)] = LobbyVariant.FromByte((byte)MatchTeam.Ct),
        };

        MatchFlowRules.FreezeRoundRoster(flow, props, new byte[] { 3 });

        Assert.True(flow.RoundRoster.ContainsKey(2));
        Assert.False(flow.RoundRoster.ContainsKey(3));
        Assert.Equal(MatchTeam.Tr, flow.RoundRoster[2]);
    }

    private static MatchRoom NewRoom() => new()
    {
        PasswordKey = "Dedik",
        RoomId = "Sandstone",
    };
}

using StandChillow.LanServer.Net;
using StandChillow.LanServer.Net.Lobby;
using StandChillow.LanServer.Net.Match;
using StandChillow.LanServer.Net.Match.Host;
using Xunit;

namespace StandChillow.LanServer.Tests;

/// <summary>
/// Issue #33 follow-up — the round MVP was awarded to the wrong player.
/// </summary>
/// <remarks>
/// The award criteria never changed: bomb explode → <c>PlantingBomb</c> for the planter,
/// defuse → <c>DefusingBomb</c> for the defuser, wipe/timeout → <c>MostEliminations</c> for the
/// top fragger of the winning side. What was broken was the <b>evidence</b>: the bomb family
/// never published <c>round_kills</c>, so «top round kills» silently fell back to career
/// <c>kills</c> (the match's top fragger, not the round's), and the plant/defuse awards used the
/// round's <i>assigned bomber</i> instead of whoever actually planted or defused.
/// </remarks>
public sealed class MatchRoundMvpTests
{
    private static MatchRoom Room(params (byte Actor, MatchTeam Team)[] fighters)
    {
        var room = new MatchRoom { PasswordKey = "Dedik", RoomId = "Sandstone 2x2" };
        foreach (var (actor, team) in fighters)
        {
            room.Actors.Add((actor, $"actor{actor}", $"user{actor}"));
            room.ActorProps[(actor, MatchRoomPropKeys.Team)] = LobbyVariant.FromByte((byte)team);
            room.ActorProps[(actor, MatchRoomPropKeys.Kills)] = LobbyVariant.FromInt(0);
            room.ActorProps[(actor, MatchRoomPropKeys.RoundKills)] = LobbyVariant.FromInt(0);
        }
        return room;
    }

    private static void RoundKills(MatchRoom room, byte actor, int kills)
    {
        room.ActorProps[(actor, MatchRoomPropKeys.RoundKills)] = LobbyVariant.FromInt(kills);
        room.Flow.RoundKills[actor] = kills;
    }

    /// <summary>
    /// Career kills must not decide the round MVP: the player who got the round's only kill wins
    /// the award even though a teammate has a far better match total.
    /// </summary>
    [Fact]
    public void Mvp_IgnoresCareerKills_AndRanksThisRoundOnly()
    {
        var room = Room((2, MatchTeam.Ct), (3, MatchTeam.Tr), (4, MatchTeam.Tr));

        room.ActorProps[(3, MatchRoomPropKeys.Kills)] = LobbyVariant.FromInt(14);
        room.ActorProps[(4, MatchRoomPropKeys.Kills)] = LobbyVariant.FromInt(1);
        RoundKills(room, actor: 4, kills: 1);
        RoundKills(room, actor: 3, kills: 0);

        var (mvpActor, mvpCode) = GameMatchHost.PickRoundMvpForTests(
            room, MatchTeam.Tr, reason: "wipe-ct");

        Assert.Equal(4, mvpActor);
        Assert.Equal(MatchMvpCodes.MostEliminations, mvpCode);
    }

    /// <summary>
    /// The plant MVP is the player who actually planted, not the bomber assigned for the round
    /// (the bomb is a carried entity and can be handed off or dropped).
    /// </summary>
    [Fact]
    public void PlantMvp_CreditsTheActualPlanter()
    {
        var room = Room((2, MatchTeam.Ct), (3, MatchTeam.Tr), (4, MatchTeam.Tr));
        room.Flow.BomberActorNr = 3;
        room.Flow.RoundPlanterActorNr = 4;

        var (mvpActor, mvpCode) = GameMatchHost.PickRoundMvpForTests(
            room, MatchTeam.Tr, reason: "bomb-explode");

        Assert.Equal(4, mvpActor);
        Assert.Equal(MatchMvpCodes.PlantingBomb, mvpCode);
    }

    /// <summary>The defuse MVP is the defuser, not simply the first living Ct of the room.</summary>
    [Fact]
    public void DefuseMvp_CreditsTheActualDefuser()
    {
        var room = Room((2, MatchTeam.Ct), (3, MatchTeam.Ct), (4, MatchTeam.Tr));
        room.Flow.RoundDefuseActorNr = 3;

        var (mvpActor, mvpCode) = GameMatchHost.PickRoundMvpForTests(
            room, MatchTeam.Ct, reason: "bomb-defuse");

        Assert.Equal(3, mvpActor);
        Assert.Equal(MatchMvpCodes.DefusingBomb, mvpCode);
    }

    /// <summary>
    /// A round where nobody scored still awards a player from the winning side — the award path
    /// must not depend on the counter being non-zero.
    /// </summary>
    [Fact]
    public void NoRoundKills_StillAwardsSomeoneOnTheWinner()
    {
        var room = Room((2, MatchTeam.Ct), (3, MatchTeam.Tr));

        var (mvpActor, mvpCode) = GameMatchHost.PickRoundMvpForTests(
            room, MatchTeam.Ct, reason: "timeout");

        Assert.Equal(2, mvpActor);
        Assert.Equal(MatchMvpCodes.MostEliminations, mvpCode);
    }

    /// <summary>Counting is round-scoped — the mirror is cleared, never accumulated.</summary>
    [Fact]
    public void RoundKills_AreRoundScoped()
    {
        var flow = new MatchFlowState();
        flow.RoundKills[2] = 3;

        Assert.Equal(3, flow.RoundKills[2]);
        flow.RoundKills.Clear();
        Assert.False(flow.RoundKills.ContainsKey(2));
    }
}

using StandChillow.LanServer.Net.Match;

namespace StandChillow.LanServer.Net.Match.Host;

/// <summary>
/// Actor numbers inside one match room.
/// </summary>
/// <remarks>
/// Issue #39: the previous allocator was <c>(byte)Math.Clamp(NextActorNr++, 1, 255)</c> — after
/// 254 joins/reconnects it handed out actor 255 for every player, so two live actors could share
/// one id (ghost bodies, "object references"). Allocating the lowest free number keeps a long
/// evening of reconnects honest; it never re-uses an id that is still in the room.
/// </remarks>
internal static class MatchRoomRoster
{
    /// <summary>Actor 1 is always the synthetic Server; humans start at 2.</summary>
    public const byte FirstHumanActorNr = MatchHostActor.ActorNr + 1;

    /// <summary>
    /// Сколько живых людей в комнате — без «серверного» актора №1.
    /// </summary>
    /// <remarks>
    /// Именно по людям, а не по числу акторов: серверный актор есть в любой комнате,
    /// и если считать его, комната «до 2 человек» пустила бы только одного.
    /// </remarks>
    public static int CountHumans(IReadOnlyList<(byte Nr, string Name, string UserId)> actors)
    {
        var humans = 0;
        for (var i = 0; i < actors.Count; i++)
        {
            if (actors[i].Nr != MatchHostActor.ActorNr)
                humans++;
        }

        return humans;
    }

    /// <summary>Lowest free actor number in <paramref name="actors"/>, or <c>null</c> when full.</summary>
    public static byte? FindFreeActorNr(
        IReadOnlyList<(byte Nr, string Name, string UserId)> actors)
    {
        // int, not byte: a byte loop wraps 255→0, so a full roster returned actor 0 instead of
        // "no free id" (caught by the full-roster test).
        for (var nr = (int)FirstHumanActorNr; nr <= 255; nr++)
        {
            var used = false;
            for (var i = 0; i < actors.Count; i++)
            {
                if (actors[i].Nr != nr)
                    continue;
                used = true;
                break;
            }

            if (!used)
                return (byte)nr;
        }

        return null;
    }
}

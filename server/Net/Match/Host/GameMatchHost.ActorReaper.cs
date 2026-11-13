using StandChillow.LanServer.Net.Match;
using StandChillow.LanServer.Net.Match.Host;

namespace StandChillow.LanServer.Net;

public sealed partial class GameMatchHost
{
    /// <summary>
    /// How long an actor may stay without an owning socket before it is reaped. Matches the match
    /// <c>DisconnectTimeout</c>: a live session is never reaped, and a reconnect in flight has the
    /// same 10 s it gets from LiteNetLib.
    /// </summary>
    private const long GhostActorGraceMs = 10_000;

    /// <summary>
    /// Issue #39: remove actors whose session is really gone.
    /// </summary>
    /// <remarks>
    /// A phone that «self-destructs» out of the room leaves its actor behind: no peer state owns
    /// it, but it stays in <see cref="MatchRoom.Actors"/> and in the team props, so the host keeps
    /// counting the player as alive, and every rejoin allocated another actor next to it — two,
    /// then three copies of one player in the room. The normal disconnect path removes an actor
    /// immediately; this sweep is the safety net for the paths that detach a peer state first
    /// (reconnect takeover, rematch reset) and then lose the socket without a matching event.
    /// </remarks>
    /// <summary>
    /// Actors that have no owning socket right now. They still hold a team prop, but nobody plays
    /// them: a round started in the grace window must not count the ghost as a living fighter
    /// (that is what kept a side «alive» forever and, on the other side, faked a wipe).
    /// </summary>
    private List<byte> GhostActorNumbers(MatchRoom room)
    {
        lock (_roomGate)
        {
            var ghosts = new List<byte>();
            foreach (var actor in room.Actors)
            {
                if (actor.Nr == MatchHostActor.ActorNr)
                    continue;
                if (MatchReconnect.IsGhostActor(room, actor.Nr, _peers.Values))
                    ghosts.Add(actor.Nr);
            }
            return ghosts;
        }
    }

    private void TickGhostActors()
    {
        var now = Environment.TickCount64;
        List<(MatchRoom Room, byte ActorNr)>? reap = null;

        lock (_roomGate)
        {
            foreach (var room in _roomsByPassword.Values.Distinct())
            {
                foreach (var actor in room.Actors)
                {
                    var actorNr = actor.Nr;
                    if (actorNr == MatchHostActor.ActorNr)
                        continue;
                    if (!MatchReconnect.IsGhostActor(room, actorNr, _peers.Values))
                    {
                        room.OwnerlessSinceTick.Remove(actorNr);
                        continue;
                    }

                    if (!room.OwnerlessSinceTick.TryGetValue(actorNr, out var sinceTick))
                    {
                        room.OwnerlessSinceTick[actorNr] = now;
                        continue;
                    }

                    if (now - sinceTick < GhostActorGraceMs)
                        continue;

                    (reap ??= new()).Add((room, actorNr));
                }
            }
        }

        if (reap is null)
            return;

        foreach (var (room, actorNr) in reap)
        {
            string? userId = null;
            string? identityKey = null;
            string? name = null;
            lock (_roomGate)
            {
                var index = room.Actors.FindIndex(a => a.Nr == actorNr);
                if (index >= 0)
                {
                    userId = room.Actors[index].UserId;
                    name = room.Actors[index].Name;
                }
                room.ActorIdentities.TryGetValue(actorNr, out identityKey);
            }

            Console.WriteLine(
                $"[match-host] ghost actor {actorNr} ('{name}') — нет владеющего сокета " +
                $"{GhostActorGraceMs / 1000}s, удаляю из комнаты (иначе он считается живым и " +
                "каждый заход плодит копию)");
            RemoveActorFromMatch(
                room, actorNr, userId, identityKey, reason: "ghost-actor-no-session");
        }
    }
}

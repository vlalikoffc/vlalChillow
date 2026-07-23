using StandChillow.LanServer.Dashboard;
using StandChillow.LanServer.Net.Lobby;
using StandChillow.LanServer.Net.Match;
using StandChillow.LanServer.Net.Match.Host;

namespace StandChillow.LanServer.Net;

public sealed partial class GameMatchHost
{
    /// <summary>
    /// Build a display-only snapshot of the active LAN match room for the CLI dashboard.
    /// Returns <c>null</c> when no room exists yet. All values come from host-tracked room /
    /// actor props — nothing is invented or read off the wire speculatively.
    /// </summary>
    public MatchSnapshot? SnapshotActiveMatch()
    {
        lock (_roomGate)
        {
            if (!_roomsByPassword.TryGetValue(LanCreateRoomPasswordKey, out var room))
                room = _roomsByPassword.Values.FirstOrDefault();
            if (room is null)
                return null;

            var isDm = string.Equals(_matchGameModeId, "DeathMatch", StringComparison.Ordinal)
                || (room.ActorProps.TryGetValue((0, MatchRoomPropKeys.C0), out var c0)
                    && c0.Kind == LobbyVariantKind.String
                    && string.Equals(c0.String, "DeathMatch", StringComparison.Ordinal));

            var modeId = room.ActorProps.TryGetValue((0, MatchRoomPropKeys.C0), out var c0v)
                && c0v.Kind == LobbyVariantKind.String && c0v.String is not null
                    ? c0v.String
                    : _matchGameModeId;
            var map = room.ActorProps.TryGetValue((0, MatchRoomPropKeys.C1), out var c1v)
                && c1v.Kind == LobbyVariantKind.String && c1v.String is not null
                    ? c1v.String
                    : _matchSelectedLevel;

            var timeDeadline = 0.0;
            if (room.ActorProps.TryGetValue((0, MatchRoomPropKeys.Time), out var t)
                && t.Kind == LobbyVariantKind.Double)
                timeDeadline = t.Double;

            var actors = new List<MatchActorSnapshot>(room.Actors.Count);
            foreach (var (nr, name) in room.Actors)
            {
                var team = MatchFlowRules.GetActorTeam(room.ActorProps, nr);
                // K column / kill attribution: prefer the explicit kills prop, fall back to the
                // client-owned fair_kills counter (DeathMatch) so a bump is still observable.
                var kills = ReadActorIntProp(room, nr, MatchRoomPropKeys.Kills);
                if (kills < 0)
                    kills = ReadActorIntProp(room, nr, MatchRoomPropKeys.FairKills);
                actors.Add(new MatchActorSnapshot
                {
                    Nr = nr,
                    Name = name,
                    Team = team,
                    Money = Math.Max(0, ReadActorIntProp(room, nr, MatchRoomPropKeys.Money)),
                    Kills = Math.Max(0, kills),
                    Assists = Math.Max(0, ReadActorIntProp(room, nr, MatchRoomPropKeys.Assists)),
                    Score = Math.Max(0, ReadActorIntProp(room, nr, MatchRoomPropKeys.Score2)),
                    DeadThisRound = MatchFlowRules.IsActorDead(room.Flow, room.ActorProps, nr),
                    Ping = ReadActorIntProp(room, nr, MatchRoomPropKeys.Ping),
                    IsHost = nr == MatchHostActor.ActorNr,
                });
            }

            return new MatchSnapshot
            {
                GameModeId = modeId,
                Map = map,
                C2 = room.RoomC2,
                Phase = room.Flow.Phase,
                Round = room.Flow.RoundIndex,
                ScoreTr = room.Flow.ScoreTr,
                ScoreCt = room.Flow.ScoreCt,
                IsDeathMatch = isDm,
                TimeDeadline = timeDeadline,
                PhaseEndsUtc = room.Flow.PhaseEndsUtc,
                Actors = actors,
            };
        }
    }

    /// <summary>Resolve an actor display name by nr (for MVP banners). Empty when unknown.</summary>
    public string ResolveActorName(byte actorNr)
    {
        lock (_roomGate)
        {
            foreach (var room in _roomsByPassword.Values)
                foreach (var (nr, name) in room.Actors)
                    if (nr == actorNr)
                        return name;
        }
        return "";
    }
}

using System.Net;
using StandChillow.LanServer.Bans;
using StandChillow.LanServer.Net.Match;

namespace StandChillow.LanServer.Net;

/// <summary>Строка админ-списка: серверный id, ник, адрес и номер актора.</summary>
public readonly record struct MatchPublicEntry(int Id, string Nick, IPAddress? Address, byte ActorNr);

/// <summary>
/// Кто сейчас в катке — с серверным id, который выводится из ника.
/// </summary>
/// <remarks>
/// <para>
/// Id не хранится и не выдаётся: он <b>вычисляется</b> (<see cref="MatchPublicId.ForNick"/>),
/// поэтому переживает реконнект, рестарт хоста и удаление каталога сервера. Здесь он
/// нужен только для админ-команд <c>/ids</c> и <c>/banid</c>.
/// </para>
/// <para>
/// В провод id не уходит: клиент его не знает и подменить не может.
/// </para>
/// </remarks>
public sealed partial class GameMatchHost
{
    /// <summary>Игроки в катке: id (из ника), ник, адрес сокета, номер актора.</summary>
    public IReadOnlyList<MatchPublicEntry> SnapshotPublicIds()
    {
        lock (_roomGate)
        {
            var entries = new List<MatchPublicEntry>();
            foreach (var room in _roomsByPassword.Values.Distinct())
            {
                foreach (var (actorNr, name, _) in room.Actors)
                {
                    if (actorNr == MatchHostActor.ActorNr || string.IsNullOrWhiteSpace(name))
                        continue;

                    var id = MatchPublicId.ForNick(name);
                    if (id == 0)
                        continue;

                    entries.Add(new MatchPublicEntry(
                        id, name, AddressOfActorLocked(room, actorNr), actorNr));
                }
            }

            return entries.OrderBy(e => e.Id).ToList();
        }
    }

    /// <summary>Найти игрока в катке по серверному id.</summary>
    public bool TryResolvePublicId(int id, out MatchPublicEntry entry)
    {
        foreach (var candidate in SnapshotPublicIds())
        {
            if (candidate.Id != id)
                continue;
            entry = candidate;
            return true;
        }

        entry = default;
        return false;
    }

    private IPAddress? AddressOfActorLocked(MatchRoom room, byte actorNr)
    {
        foreach (var (peer, state) in _peers)
        {
            if (ReferenceEquals(state.Room, room) && state.ActorNr == actorNr)
                return peer.Address;
        }

        return null;
    }
}

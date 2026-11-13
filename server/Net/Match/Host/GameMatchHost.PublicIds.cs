using System.Net;
using StandChillow.LanServer.Bans;
using StandChillow.LanServer.Net.Match;
using StandChillow.LanServer.Net.Match.Host;
using StandChillow.LanServer.Net.Lobby;

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

    /// <summary>
    /// Подменять ли клиентский <c>uid</c> («Offline») на серверный id игрока. По умолчанию да:
    /// тогда в игре вместо заглушки виден номер, который принимает <c>/banid</c>.
    /// </summary>
    public bool ExposeServerUid { get; set; } = true;

    /// <summary>
    /// Новое значение <c>uid</c> для автора свойства: постоянный id его ника.
    /// </summary>
    /// <remarks>
    /// Ник берём из состояния сессии (<c>RosterName</c> после JoinRoom), а не из пакета:
    /// свойство может прислать кто угодно и про любого актора, а id должен быть тем же,
    /// что показал <c>/ids</c>. Актор 1 (Server) — не человек, его «Offline» не трогаем.
    /// </remarks>
    private bool TryServerUid(byte actorNr, MatchPeerState state, MatchRoom room, out LobbyVariant uid)
    {
        uid = default;
        if (!ExposeServerUid || actorNr == MatchHostActor.ActorNr)
            return false;

        var nick = actorNr == state.ActorNr && ReferenceEquals(state.Room, room)
            ? state.RosterName
            : null;

        if (string.IsNullOrWhiteSpace(nick))
        {
            // Свойства может прислать хост за игрока — тогда его состояния под рукой нет
            // и ник ищем по сессиям комнаты (после JoinRoom он уже записан).
            foreach (var other in _peers.Values)
            {
                if (other.ActorNr != actorNr || !ReferenceEquals(other.Room, room))
                    continue;
                if (string.IsNullOrWhiteSpace(other.RosterName))
                    continue;
                nick = other.RosterName;
                break;
            }
        }

        if (!MatchPublicId.TryWireUid(nick, out var text))
            return false;

        uid = LobbyVariant.FromString(text);
        return true;
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

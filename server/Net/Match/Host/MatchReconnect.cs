using StandChillow.LanServer.Net.Lobby;
using StandChillow.LanServer.Net.Match;

namespace StandChillow.LanServer.Net.Match.Host;

/// <summary>
/// Issue #39 — «тот же игрок вернулся»: поиск его actor и передача actor новому сокету
/// без пересоздания.
/// </summary>
/// <remarks>
/// Живёт отдельно от <see cref="GameMatchHost"/> как чистые функции над комнатой и
/// peer-состояниями: сокет не нужен, поэтому поведение покрывается обычными тестами.
/// Ключ сравнения — <see cref="MatchPeerState.IdentityKey"/>, а не wire-<c>uid</c>:
/// у 2.07 uid содержит гуид сессии и меняется при каждом переподключении.
/// </remarks>
internal static class MatchReconnect
{
    /// <summary>
    /// Actor того же игрока в этой комнате (<c>0</c> — не найден). Ищется только среди
    /// живых peer-состояний: если старый сокет уже отвалился по таймауту, его actor удалён,
    /// и вернувшемуся законно выдаётся новый номер.
    /// </summary>
    public static byte FindActorNr(
        MatchRoom room,
        IEnumerable<MatchPeerState> peers,
        string? identityKey,
        out MatchPeerState? state)
    {
        state = null;
        if (string.IsNullOrEmpty(identityKey))
            return 0;

        foreach (var st in peers)
        {
            if (!ReferenceEquals(st.Room, room) || st.ActorNr == 0)
                continue;
            if (!MatchIdentity.SamePlayer(st.IdentityKey, identityKey))
                continue;
            state = st;
            return st.ActorNr;
        }

        return 0;
    }

    /// <summary>
    /// Передать actor вернувшемуся игроку. Ничего не пересоздаётся: тот же номер, имя,
    /// room-свойства и место в round-roster; меняется владелец и wire-uid новой сессии.
    /// </summary>
    /// <returns>Команда, которую надо восстановить после INIT (None — нечего).</returns>
    public static MatchTeam Adopt(
        MatchRoom room,
        MatchPeerState newState,
        MatchPeerState previousState,
        byte actorNr)
    {
        if (ReferenceEquals(previousState, newState))
            return MatchTeam.None;

        var remembered = MatchTeam.None;
        if (room.ActorProps.TryGetValue((actorNr, MatchRoomPropKeys.Team), out var teamValue)
            && teamValue.Kind == LobbyVariantKind.Byte)
        {
            remembered = (MatchTeam)teamValue.Byte;
        }

        var index = room.Actors.FindIndex(a => a.Nr == actorNr);
        var rosterName = index >= 0 ? room.Actors[index].Name : $"actor{actorNr}";
        if (index >= 0)
            room.Actors[index] = (actorNr, rosterName, newState.UserId ?? "");

        // В комнате лежит wire-uid сессии; при возврате он меняется на новый токен.
        if (previousState.UserId is { Length: > 0 } previousUid)
            room.UserIds.Remove(previousUid);
        if (newState.UserId is { Length: > 0 } newUid)
            room.UserIds.Add(newUid);

        // Новый pawn должен прийти заново: SpawnedPawns — доказательство respawn для
        // MaybeScheduleReconnectRestore. Старый pawn не уничтожаем — его заменит CWO.
        room.SpawnedPawns.Remove(actorNr);

        // Старый сокет больше не владеет actor: его пакеты игнорируются, а будущий
        // Disconnect не удалит живого игрока (ActorNr=0).
        previousState.Room = null;
        previousState.ActorNr = 0;

        newState.Room = room;
        newState.ActorNr = actorNr;
        newState.RosterName = rosterName;
        newState.PendingReconnectTeam =
            remembered is MatchTeam.Tr or MatchTeam.Ct ? remembered : null;
        return remembered;
    }
}

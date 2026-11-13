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
    /// Передать actor вернувшемуся игроку. Номер, имя и room-свойства сохраняются, но
    /// боевое состояние старой сессии — нет: старый pawn, флаг смерти в раунде и место в
    /// frozen roster удаляются. Иначе сервер «помнит» игрока живым (или мёртвым) с чужим
    /// телом и потом выкидывает его в Spectator (issue #39).
    /// </summary>
    public static ReconnectAdoption Adopt(
        MatchRoom room,
        MatchPeerState newState,
        MatchPeerState previousState,
        byte actorNr)
    {
        if (ReferenceEquals(previousState, newState))
            return new ReconnectAdoption(MatchTeam.None, Array.Empty<short>());

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

        // Старое тело больше не принадлежит этому игроку: убираем его из трекинга и просим
        // пиров удалить (host-authored Destroy). Новый pawn придёт своим CWO.
        var pawnIds = room.LivingPawns
            .Where(kv => kv.Value.OwnerActorNr == actorNr)
            .Select(kv => kv.Key)
            .ToList();
        foreach (var id in pawnIds)
            room.LivingPawns.Remove(id);
        room.SpawnedPawns.Remove(actorNr);

        // «Мёртв/жив» в текущем раунде — состояние старой сессии, ему верить нельзя.
        // Возвращается как cold start: вне frozen roster, до следующего C2=22 (та же
        // политика, что у обычного mid-round joiner).
        room.Flow.DeadActors.Remove(actorNr);
        MatchFlowRules.RemoveFromRoundRoster(room.Flow, actorNr);

        // Старый сокет больше не владеет actor: его пакеты игнорируются, а будущий
        // Disconnect не удалит живого игрока (ActorNr=0).
        previousState.Room = null;
        previousState.ActorNr = 0;

        newState.Room = room;
        newState.ActorNr = actorNr;
        newState.RosterName = rosterName;
        newState.PendingReconnectTeam =
            remembered is MatchTeam.Tr or MatchTeam.Ct ? remembered : null;
        // Identity batch от вернувшегося клиента не приходит — INIT не ждём.
        newState.ReconnectIdentityTrusted = true;
        return new ReconnectAdoption(remembered, pawnIds);
    }
}

/// <summary>Что забрал <see cref="MatchReconnect.Adopt"/>: память о команде и старые тела.</summary>
internal readonly record struct ReconnectAdoption(
    MatchTeam RememberedTeam,
    IReadOnlyList<short> RemovedPawnIds);

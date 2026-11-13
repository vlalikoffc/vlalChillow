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
    /// Actor того же игрока в этой комнате (<c>0</c> — не найден).
    /// </summary>
    /// <remarks>
    /// Сначала смотрим <see cref="MatchRoom.ActorIdentities"/> — владелец живёт в комнате, а не в
    /// сокете. Иначе actor, чей сокет уже отвалился, становится недостижимым призраком: игроку
    /// выдают <b>второй</b> номер рядом с ним (в комнате две копии, призрак считается живым),
    /// и каждый следующий заход добавляет ещё одну.
    /// <para>
    /// <paramref name="state"/> остаётся <c>null</c> для призрака — отцеплять нечего, но actor
    /// всё равно возвращается владельцу.
    /// </para>
    /// </remarks>
    public static byte FindActorNr(
        MatchRoom room,
        IEnumerable<MatchPeerState> peers,
        string? identityKey,
        out MatchPeerState? state)
    {
        state = null;
        if (string.IsNullOrEmpty(identityKey))
            return 0;

        foreach (var (actorNr, key) in room.ActorIdentities)
        {
            if (!MatchIdentity.SamePlayer(key, identityKey))
                continue;
            foreach (var st in peers)
            {
                if (ReferenceEquals(st.Room, room) && st.ActorNr == actorNr)
                {
                    state = st;
                    break;
                }
            }
            return actorNr;
        }

        // Комнаты без карты идентичностей (состояние до #39) — старый поиск по сокетам.
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

    /// <summary>У актёра нет владеющего сокета в этой комнате — сессия пропала.</summary>
    public static bool IsGhostActor(MatchRoom room, byte actorNr, IEnumerable<MatchPeerState> peers)
    {
        foreach (var st in peers)
        {
            if (ReferenceEquals(st.Room, room) && st.ActorNr == actorNr)
                return false;
        }
        return true;
    }

    /// <summary>
    /// Передать actor вернувшемуся игроку. Номер, имя и room-свойства сохраняются, но
    /// боевое состояние старой сессии — нет: старый pawn и отложенная смерть от Destroy
    /// убираются. Дальше политика зависит от того, заморожен ли уже состав раунда:
    /// <list type="bullet">
    /// <item>состав не заморожен (закуп/ожидание) — раундовая смерть сбрасывается, клиент
    /// может появиться снова до старта боя;</item>
    /// <item>состав заморожен (бой идёт) — игрок выбывает из текущего раунда, как при
    /// обычном disconnect; сервер не считает его живым до следующего закупа.</item>
    /// </list>
    /// <paramref name="cumulativeDeathCounters"/> (DeathMatch / Duel FFA) сохраняет
    /// накопительный <c>death</c>: это счётчик смертей за матч, а не флаг «жив в раунде».
    /// </summary>
    public static ReconnectAdoption Adopt(
        MatchRoom room,
        MatchPeerState newState,
        MatchPeerState? previousState,
        byte actorNr,
        bool cumulativeDeathCounters = false)
    {
        if (previousState is not null && ReferenceEquals(previousState, newState))
            return new ReconnectAdoption(MatchTeam.None, Array.Empty<short>(), ClearedRoundDeath: false);

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
        if (previousState?.UserId is { Length: > 0 } previousUid)
            room.UserIds.Remove(previousUid);
        if (newState.UserId is { Length: > 0 } newUid)
            room.UserIds.Add(newUid);
        // Владелец actor — этот игрок; призрак больше не призрак.
        if (newState.IdentityKey is { Length: > 0 } identityKey)
            room.ActorIdentities[actorNr] = identityKey;
        room.OwnerlessSinceTick.Remove(actorNr);

        // Старое тело больше не принадлежит этому игроку: убираем его из трекинга и просим
        // пиров удалить (host-authored Destroy). Новый pawn придёт своим CWO.
        var pawnIds = room.LivingPawns
            .Where(kv => kv.Value.OwnerActorNr == actorNr)
            .Select(kv => kv.Key)
            .ToList();
        foreach (var id in pawnIds)
        {
            room.LivingPawns.Remove(id);
            // Issue #40: the takeover Destroy fan-out skips the returning socket (it is not
            // INIT-ready yet). Queue the same ids for its post-gate cleanup so a retained
            // client scene cannot keep the old local body as a ghost.
            newState.PendingWorldCleanupIds.Add(id);
        }
        room.SpawnedPawns.Remove(actorNr);

        // Stale session bookkeeping must not outlive the socket: a Destroy grace armed for
        // the old body, or damage attribution to/from it, can otherwise author a death or
        // award a kill after the player already returned.
        room.Flow.PendingCombatDestroy.Remove(actorNr);
        room.LastCombatDamage.Remove(actorNr);
        foreach (var attacker in room.LastCombatDamage
                     .Where(kv => kv.Value.Victim == actorNr)
                     .Select(kv => kv.Key)
                     .ToList())
        {
            room.LastCombatDamage.Remove(attacker);
        }

        // «Мёртв/жив» в текущем раунде — состояние старой сессии, ему верить нельзя.
        // Until combat freezes the roster (buy / waiting), clear the round-local death so
        // the returning fighter can respawn before the round starts. Once the roster is
        // frozen, he leaves this round exactly like a disconnect; the caller re-checks wipe
        // so an empty side cannot hang the round. Cumulative death counters are preserved.
        var roundFrozen = room.Flow.RoundRoster.Count > 0;
        var clearedRoundDeath = false;
        if (!cumulativeDeathCounters && !roundFrozen)
        {
            room.Flow.DeadActors.Remove(actorNr);
            room.ActorProps[(actorNr, MatchRoomPropKeys.Death)] = LobbyVariant.FromInt(0);
            clearedRoundDeath = true;
        }
        MatchFlowRules.RemoveFromRoundRoster(room.Flow, actorNr);

        // Старый сокет больше не владеет actor: его пакеты игнорируются, а будущий
        // Disconnect не удалит живого игрока (ActorNr=0).
        if (previousState is not null)
        {
            previousState.Room = null;
            previousState.ActorNr = 0;
        }

        newState.Room = room;
        newState.ActorNr = actorNr;
        newState.RosterName = rosterName;
        newState.PendingReconnectTeam =
            remembered is MatchTeam.Tr or MatchTeam.Ct ? remembered : null;
        // Identity batch от вернувшегося клиента не приходит — INIT не ждём.
        newState.ReconnectIdentityTrusted = true;
        return new ReconnectAdoption(remembered, pawnIds, clearedRoundDeath);
    }
}

/// <summary>Что забрал <see cref="MatchReconnect.Adopt"/>: память о команде и старые тела.</summary>
internal readonly record struct ReconnectAdoption(
    MatchTeam RememberedTeam,
    IReadOnlyList<short> RemovedPawnIds,
    bool ClearedRoundDeath);

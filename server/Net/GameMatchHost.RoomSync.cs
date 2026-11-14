using StandChillow.LanServer.Net.Match;
using StandChillow.LanServer.Net.Match.Kernel;

namespace StandChillow.LanServer.Net;

public sealed partial class GameMatchHost
{
    /// <summary>
    /// Перенести состояние комнат в реестр панели: сколько людей и какая фаза.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Панель не читает <c>_roomsByPassword</c> напрямую: это горячая структура игрового
    /// цикла, и HTTP-поток не должен её блокировать. Реестр — отдельная маленькая копия,
    /// которую обновляет сам хост, раз в секунду.
    /// </para>
    /// <para>
    /// Перевод фаз: до <see cref="MatchFlowPhase.Warmup"/> включительно комната ещё
    /// «ждёт» — играть в неё можно, игроков добирают. С <see cref="MatchFlowPhase.PurchasePhase"/>
    /// идёт катка. <see cref="MatchFlowPhase.MatchOver"/> и финальные стадии DeathMatch —
    /// «закончилась», такие комнаты удаляются по TTL.
    /// </para>
    /// </remarks>
    internal void SyncRoomRegistry()
    {
        var registry = Rooms;
        if (registry is null)
            return;

        List<(int Number, RoomState State, int Players)> snapshot = new();
        lock (_roomGate)
        {
            foreach (var room in _roomsByPassword.Values)
            {
                if (!room.IsPanelRoom || room.DisplayNumber <= 0)
                    continue;

                var humans = 0;
                foreach (var (nr, _, _) in room.Actors)
                    if (nr != MatchHostActor.ActorNr)
                        humans++;

                snapshot.Add((room.DisplayNumber, MapPhase(room.Flow.Phase, humans), humans));
            }
        }

        foreach (var (number, state, players) in snapshot)
            registry.TryUpdate(number, state, players);
    }

    /// <summary>
    /// Фаза матча → состояние комнаты для панели.
    /// </summary>
    /// <remarks>
    /// Ни одна ветка не выдумывает данных: «идёт» — только те фазы, в которых бой
    /// действительно идёт, «закончилась» — только явный конец. Всё остальное «ждёт»,
    /// даже если в комнате никого нет: удалит её TTL, а не выдуманный статус.
    /// </remarks>
    internal static RoomState MapPhase(MatchFlowPhase phase, int humans)
    {
        switch (phase)
        {
            case MatchFlowPhase.PurchasePhase:
            case MatchFlowPhase.RoundLive:
            case MatchFlowPhase.BombPlanted:
            case MatchFlowPhase.RoundEndPause:
            case MatchFlowPhase.DeathMatchLive:
            case MatchFlowPhase.DeathMatchWarmup:
            case MatchFlowPhase.HalfTimeIntro:
            case MatchFlowPhase.HalfTimeSwap:
            case MatchFlowPhase.HalfTimeTransition:
                return RoomState.Live;

            case MatchFlowPhase.MatchOver:
            case MatchFlowPhase.DeathMatchEnded:
            case MatchFlowPhase.DeathMatchFinalHud:
                return RoomState.Finished;

            default:
                // WaitingPlayers / Warmup / WarmupWillFinish / AlliesPreWarmup.
                // Пустая комната «ждёт» столько, сколько ей даёт TTL.
                _ = humans;
                return RoomState.Waiting;
        }
    }
}

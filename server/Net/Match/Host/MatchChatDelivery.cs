namespace StandChillow.LanServer.Net.Match.Host;

/// <summary>
/// Доставка чата катки (ChatManager, scene id=7). Без сокетов — правило покрывают тесты,
/// как у <see cref="MatchBombRpcDelivery"/> и <see cref="WorldObjectRpcRelayPolicy"/>.
/// </summary>
/// <remarks>
/// Отчёт: «сервер видит мои команды, но не исполняет и не пишет ничего в ответ в чат матча;
/// свои сообщения в матч не шлёт вовсе». Разбор <c>captures/chat.zip</c> (проба-клиент у
/// телефон-хоста, 4326 кадров) показывает: чат катки — это <b>только</b>
/// <c>WorldObjectRpc(203)</c> на ChatManager. Ни <c>WorldObjectState</c>, ни
/// <c>SetProperty</c> на id=7 в сессии нет, а <c>ReCreateSceneManager</c> пересоздаёт
/// 4/5/6/8 — ChatManager не трогает. Другого канала у матча нет.
/// <para>
/// Именно этот опкод хост дропает на бэкпрешере: <c>IsDroppableWhenBacklogged</c> считает
/// <c>WorldObjectRpc</c> косметикой (позы спавна устаревают на следующем кадре). Для чата
/// это неверно — клиент не рисует сообщение сам, он ждёт кадр от хоста (README: эхо автору,
/// без него окно чата выглядит мёртвым). Дроп одного Rpc = навсегда потерянный пузырь, а для
/// серверного ответа — ровно «не шлёт вовсе»: <c>Send</c> возвращает false, доставка равна 0,
/// и широкий путь (без ника) об этом даже не логировал. Поэтому чат идёт недропаемым путём,
/// тем же, что решающие BombManager Rpc (#33).
/// </para>
/// </remarks>
internal static class MatchChatDelivery
{
    /// <summary>Rpc на ChatManager (id=7) — текст чата катки.</summary>
    public static bool IsChatRpc(short objectId) =>
        objectId == MatchSceneManagers.ChatManagerObjectId;

    /// <summary>
    /// Rpc, которые нельзя дропать отстающему пиру: чат катки и BombManager
    /// (plant / defuse / explode). Остальное тем же опкодом — косметика (позы, эффекты).
    /// </summary>
    public static bool IsStructuralRpc(short objectId) =>
        IsChatRpc(objectId) || objectId == MatchFlowTestParams.BombManagerObjectId;

    /// <summary>
    /// Почему серверное сообщение не дошло ни до одного пира — честная строка в журнал.
    /// Без неё «peers=0» выглядело как «сервер ничего не отправил», хотя причина всегда
    /// одна из трёх: в катке никого нет, пир ещё не получил bootstrap, или его сцена ещё
    /// строится и трафик держит <see cref="MatchWorldGate"/>.
    /// </summary>
    public static string DescribeNoTarget(int peersInRoom, int initReady, int gateHeld)
    {
        if (peersInRoom <= 0)
            return "в катке нет пиров";
        if (initReady <= 0 && gateHeld > 0)
            return $"{gateHeld} пир(а) ещё строят сцену — world-gate держит трафик (до {MatchWorldGate.HoldMs / 1000} с)";
        if (initReady <= 0)
            return $"{peersInRoom} пир(а) без bootstrap — INIT не разблокирован";
        return $"{peersInRoom} пир(а), {initReady} готовы, но адресат не совпал";
    }
}

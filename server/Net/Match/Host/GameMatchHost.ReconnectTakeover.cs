using StandChillow.LanServer.Net.Match;
using StandChillow.LanServer.Net.Match.Host;

namespace StandChillow.LanServer.Net;

public sealed partial class GameMatchHost
{
    /// <summary>
    /// Issue #39: actor, который принадлежит тому же игроку в этой же комнате.
    /// </summary>
    /// <remarks>
    /// Сравнение — по устойчивой части 2.07 auth-токена (<see cref="MatchPeerState.IdentityKey"/>),
    /// а не по полному токену: гуид сессии новый на каждом подключении.
    /// </remarks>
    private bool TryFindRejoinActor(
        MatchRoom room,
        string identityKey,
        out byte actorNr,
        out MatchPeerState? previousState)
    {
        lock (_roomGate)
            actorNr = MatchReconnect.FindActorNr(room, _peers.Values, identityKey, out previousState);
        return actorNr != 0 && previousState is not null;
    }

    /// <summary>
    /// Issue #39: вернуть тому же игроку его прежний actor вместо второго.
    /// </summary>
    /// <remarks>
    /// Сценарий бага: телефон теряет сеть, клиент сам переподключается к катке (без выхода
    /// в лобби) и присылает новый handshake с новым сессионным токеном. Старый сокет ещё
    /// живёт до <c>DisconnectTimeout</c> (10 с), поэтому в комнате остаётся его actor. Хост
    /// сравнивал полный токен, не узнавал игрока и выдавал <b>второй</b> actor — у клиента
    /// в <c>OnJoinRoomResponse</c> летел <c>NullReferenceException</c>, и катка «висела».
    /// <para>
    /// Никаких <c>ActorLeft</c>/<c>ActorJoined</c> другим пирам не отправляется — для них
    /// игрок не выходил. Подробности передачи actor — <see cref="MatchReconnect.Adopt"/>.
    /// </para>
    /// </remarks>
    private void AdoptReconnectingActor(
        MatchRoom room,
        MatchPeerState newState,
        byte actorNr,
        MatchPeerState previousState)
    {
        if (ReferenceEquals(previousState, newState))
        {
            // Тот же UDP-пир повторяет JoinRoom (retry) — возвращаем actor как есть.
            Console.WriteLine(
                $"[match-host] reconnect-same-peer: actor={actorNr} повторный JoinRoom с того же " +
                "сокета — Found без изменений");
            return;
        }

        var previousUid = previousState.UserId;
        ReconnectAdoption adoption;
        lock (_roomGate)
            adoption = MatchReconnect.Adopt(room, newState, previousState, actorNr);

        try { previousState.BootstrapTimeoutCts?.Cancel(); }
        catch { /* ignore */ }

        // Issue #39: the old body belongs to a session that no longer exists. Leaving it in
        // the world made the host count a dead (or absent) player as alive and then force him
        // to Spectator. Peers are told to drop it; the returning client creates a new pawn.
        foreach (var pawnId in adoption.RemovedPawnIds)
        {
            var destroy = MatchCodec.BuildDestroyWorldObject(NextServerTime(), pawnId);
            var peers = BroadcastInitReady(room, destroy, tag: "match_tx_DestroyWorldObject");
            Console.WriteLine(
                $"[match-host] TX DestroyWorldObject id={pawnId} → peers={peers} " +
                $"(reconnect actor={actorNr}: old body removed, not counted alive)");
        }

        var remembered = adoption.RememberedTeam;
        Console.WriteLine(
            $"[match-host] reconnect-takeover: identity='{newState.IdentityKey}' actor={actorNr} " +
            $"('{newState.RosterName}') перешёл к новой сессии uid='{newState.UserId}' — " +
            $"старый uid='{previousUid}' отсоединён, второго actor нет" +
            (remembered is MatchTeam.Tr or MatchTeam.Ct
                ? $"; restore team={remembered} после INIT"
                : "; team не запомнена (Spectator/нет)"));
    }
}

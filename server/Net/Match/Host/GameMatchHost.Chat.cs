using LiteNetLib;
using StandChillow.LanServer.Config;
using StandChillow.LanServer.Net.Match;
using StandChillow.LanServer.Net.Match.Host;

namespace StandChillow.LanServer.Net;

/// <summary>
/// Голос сервера в катке: серверный чат (ChatManager, scene id=7), адресная доставка
/// приватных ответов и диагностика «кто сейчас в катке». Вынесено из
/// <c>GameMatchHost.cs</c>, чтобы не растить файл за предел ~1000 строк (CONTRIBUTING).
/// </summary>
/// <remarks>
/// Канал один: <c>WorldObjectRpc(203)</c> на ChatManager. В <c>captures/chat.zip</c>
/// (проба-клиент у телефон-хоста, 4326 кадров) на id=7 нет ни <c>WorldObjectState</c>, ни
/// <c>SetProperty</c>, а <c>ReCreateSceneManager</c> пересоздаёт 4/5/6/8 — чат не трогает.
/// Поэтому чат идёт недропаемым путём (<see cref="MatchChatDelivery"/>), а недоставленное
/// сообщение называется в журнале по причине, а не исчезает молча.
/// </remarks>
public sealed partial class GameMatchHost
{
    /// <summary>
    /// Server-authored chat inside the match. Unlike <see cref="OnServerDebugChat"/> this is not
    /// gated by debug flags — it is the host's real voice in the катка, and it reaches phones on
    /// the <b>7777</b> socket, where lobby OpChat cannot.
    /// </summary>
    /// <remarks>
    /// Phones talk in a match through ChatManager (scene object id=7) and the host relays those
    /// <c>WorldObjectRpc</c> packets. The lobby-side <c>BroadcastServerChat</c> builds a lobby
    /// OpChat event instead, so a player in the катка never saw anything the server said —
    /// they only saw each other, because their own chat was relayed inside the match. This
    /// method puts the server on the same wire the phones use.
    /// </remarks>
    /// <param name="text">Message body.</param>
    /// <param name="nick">
    /// When set, only that roster nick gets the bubble (private command feedback must not leak
    /// into the shared match chat). <c>null</c>/empty — everyone in the match.
    /// </param>
    /// <param name="alsoLastChanceIp">
    /// Address of the lobby peer that asked, when this is command feedback. The game client keeps
    /// its lobby socket open for the whole match, so a reply that only made it there looked
    /// delivered on the server and was invisible in the катка. Given a hint, an unmatchable
    /// nick
    /// still reaches one plausible requester (same IP → has a pawn → the only peer) instead of
    /// being swallowed. Never broadcasts: a private reply must not surface in the shared chat.
    /// </param>
    /// <returns>Number of match peers the bubble was sent to.</returns>
    public int SendServerChat(
        string text, string? nick = null, System.Net.IPAddress? alsoLastChanceIp = null)
    {
        text = text.Trim();
        if (text.Length == 0)
            return 0;

        var target = ServerConfig.NormalizeNick(nick);
        var sent = 0;
        List<MatchRoom> rooms;
        lock (_roomGate)
            rooms = _roomsByPassword.Values.Distinct().ToList();

        foreach (var room in rooms)
        {
            if (target.Length == 0)
            {
                // Недропаемый путь: чат — не косметика (см. MatchChatDelivery). Дроп на
                // бэкпрешере выглядел как «сервер вовсе не шлёт в матч»: Send возвращает
                // false, доставка 0, и эта ветка (без ника) про это даже не логировала.
                var broadcast = MatchCodec.BuildChatManagerText(NextServerTime(), text);
                var n = BroadcastInitReadyCritical(
                    room, broadcast, tag: "match_tx_ChatManager_server");
                sent += n;
                if (n == 0)
                {
                    var (inRoom, ready, held) = CountChatTargets(room);
                    Console.WriteLine(
                        $"[chat] Server → match: '{text}' — никто не получил " +
                        $"({MatchChatDelivery.DescribeNoTarget(inRoom, ready, held)})");
                }

                continue;
            }

            var payload = MatchCodec.BuildChatManagerText(NextServerTime(), text);
            foreach (var (p, st) in _peers)
            {
                if (st.Room != room || !st.BootstrapSent)
                    continue;
                if (!string.Equals(
                        ServerConfig.NormalizeNick(st.RosterName),
                        target,
                        StringComparison.OrdinalIgnoreCase))
                    continue;
                if (SendCritical(p, payload))
                {
                    sent++;
                    // Нашли адресата — второй раз в ту же комнату не шлём, иначе он
                    // получит два одинаковых пузыря при повторном совпадении ника.
                    break;
                }
            }
        }

        if (sent == 0 && alsoLastChanceIp is not null && target.Length > 0)
            sent = SendServerChatLastChance(text, alsoLastChanceIp);

        return sent;
    }

    /// <summary>
    /// Сколько в комнате адресатов у чата: всего пиров, сколько прошло bootstrap и не держится
    /// world-gate'ом, и сколько сейчас за гейтом (сцена ещё строится). Нужно для честной строки
    /// «никто не получил» — без неё <c>peers=0</c> не отличить от «сервер ничего не отправил».
    /// </summary>
    private (int InRoom, int InitReady, int GateHeld) CountChatTargets(MatchRoom room)
    {
        int inRoom = 0, ready = 0, held = 0;
        foreach (var st in _peers.Values)
        {
            if (st.Room != room)
                continue;
            inRoom++;
            if (!st.BootstrapSent)
                continue;
            if (MatchWorldGate.HoldsTraffic(st.BootstrapSent, st.WorldReady))
            {
                held++;
                continue;
            }

            ready++;
        }

        return (inRoom, ready, held);
    }

    /// <summary>
    /// Кто сейчас в катке — для лога доставки: «ждал ник X, а в катке вот кто».
    /// </summary>
    /// <remarks>
    /// Без этого строка «ответ только в лобби» ничего не объясняла: ни кто подключён, ни
    /// готов ли его bootstrap (а он ждёт identity до 15 с).
    /// </remarks>
    public string DescribePeers()
    {
        var parts = new List<string>();
        foreach (var (p, st) in _peers)
        {
            parts.Add(
                $"{st.ActorNr}/{(string.IsNullOrWhiteSpace(st.RosterName) ? "?" : st.RosterName)}" +
                $"@{p.Address}{(st.BootstrapSent ? "" : "(bootstrap ждёт)")}");
        }

        return parts.Count == 0 ? "в катке нет пиров" : string.Join(", ", parts);
    }

    /// <summary>
    /// Last resort for command feedback: one match peer that plausibly is the requester.
    /// Never broadcasts — a private reply must not surface in the shared match chat.
    /// </summary>
    private int SendServerChatLastChance(string text, System.Net.IPAddress peerIp)
    {
        List<MatchRoom> rooms;
        lock (_roomGate)
            rooms = _roomsByPassword.Values.Distinct().ToList();
        foreach (var room in rooms)
        {
            var candidates = new List<(NetPeer Peer, MatchPeerState State)>();
            foreach (var (p, st) in _peers)
            {
                if (st.Room != room || !st.BootstrapSent)
                    continue;
                candidates.Add((p, st));
            }
            if (candidates.Count == 0)
                continue;
            // Prefer the peer that came from the same machine as the lobby requester.
            var sameIp = candidates
                .Where(c => SameAddress(c.Peer.Address, peerIp))
                .ToList();

            // Один пир в катке — это заведомо он. Больше одного и ни одного совпадения по
            // IP — не угадываем: приватный ответ не должен уйти чужому человеку.
            if (sameIp.Count == 0 && candidates.Count > 1)
            {
                Console.WriteLine(
                    $"[chat] last-chance: ip={peerIp} не совпал ни с кем из {candidates.Count} " +
                    "пиров — ответ не отправляю (мог бы уйти чужому)");
                return 0;
            }

            var pool = sameIp.Count > 0 ? sameIp : candidates;
            // Then whoever already has a pawn — an observer without a pawn is the wrong target.
            var spawned = pool.Where(c => room.SpawnedPawns.Contains(c.State.ActorNr)).ToList();
            var final = spawned.Count > 0 ? spawned : pool;
            // Two phones on one PC share an IP; that is still the requester, so take the lowest
            // actor instead of swallowing the reply (exactly one peer — no double bubble).
            if (final.Count != 1)
            {
                Console.WriteLine(
                    $"[chat] last-chance: peers={final.Count} ip={peerIp}, беру actor " +
                    $"{final[0].State.ActorNr}");
                final = final.OrderBy(c => c.State.ActorNr).ToList();
            }
            var target = final[0].State;
            var payload = MatchCodec.BuildChatManagerText(NextServerTime(), text);
            if (!SendCritical(final[0].Peer, payload))
                return 0;
            Console.WriteLine(
                $"[chat] Server → actor={target.ActorNr} " +
                $"(match, last-chance by ip={peerIp}): {text}");
            return 1;
        }
        return 0;
    }

    private static bool SameAddress(System.Net.IPAddress a, System.Net.IPAddress b) =>
        a.Equals(b) || a.MapToIPv4().Equals(b.MapToIPv4());
}

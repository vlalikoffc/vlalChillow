using LiteNetLib;
using StandChillow.LanServer.Config;
using StandChillow.LanServer.Diagnostics;
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
/// <para>
/// Автор серверного кадра — живой актор из комнаты, а не актор сервера: кадр, подписанный
/// актором 1 («Server» в Spectator, без пешки), клиент не рисует вовсе. Это наблюдение, а не
/// догадка — проба <c>/chattest</c> (latest-3.log, 2026-11-14) из четырёх вариантов показала
/// на экране только C: живой автор и <c>field=1</c>. Подробности и выбор автора —
/// <see cref="MatchChatAuthor"/>.
/// </para>
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
                // Недропаемый путь: чат — не косметика (см. MatchChatDelivery). Кадр строим
                // на каждого получателя отдельно — подписать его надо живым актором, который
                // не сам получатель (MatchChatAuthor: наблюдение пробы /chattest).
                var n = BroadcastServerChat(room, text);
                sent += n;
                if (n == 0)
                {
                    var (inRoom, ready, held) = CountChatTargets(room);
                    var reason = MatchChatDelivery.DescribeNoTarget(inRoom, ready, held);
                    // До старта катки в комнате никого нет, а лобби-строки (ростер,
                    // приветствия) зеркалятся сюда постоянно — логировать каждый такой
                    // промах значит вытеснить из журнала всё остальное (latest.log: 170
                    // строк «Server → match», из них 63 — «в катке нет пиров»).
                    if (inRoom > 0)
                    {
                        LogChatMiss(
                            $"match:{FirstLine(text)}",
                            reason,
                            $"[chat] Server → match: '{FirstLine(text)}' — никто не получил ({reason})");
                    }
                }

                continue;
            }

            var live = LiveChatActors(room);
            foreach (var (p, st) in _peers)
            {
                if (st.Room != room || !st.BootstrapSent)
                    continue;
                if (!string.Equals(
                        ServerConfig.NormalizeNick(st.RosterName),
                        target,
                        StringComparison.OrdinalIgnoreCase))
                    continue;
                if (SendCritical(p, BuildServerChatPayload(text, st.ActorNr, live)))
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

    /// <summary>Есть ли в катке хоть один пир (даже если его сцена ещё строится).</summary>
    public bool HasMatchPeers => !_peers.IsEmpty;

    /// <summary>Первая строка текста — ростер приходит многострочным, в журнал хватит заголовка.</summary>
    private static string FirstLine(string text)
    {
        var nl = text.IndexOf('\n');
        return nl < 0 ? text : text[..nl].TrimEnd('\r');
    }

    /// <summary>
    /// Серверный текст всем в комнате: тот же недропаемый путь и те же фильтры, что у
    /// <c>BroadcastInitReadyCritical</c> (INIT-ready, вне world-gate), но кадр на каждого
    /// получателя свой — автор выбирается под получателя (<see cref="MatchChatAuthor"/>).
    /// </summary>
    private int BroadcastServerChat(MatchRoom room, string text)
    {
        var live = LiveChatActors(room);
        var n = 0;
        byte[]? sample = null;
        foreach (var (p, st) in _peers)
        {
            if (st.Room != room || !st.BootstrapSent)
                continue;
            if (MatchWorldGate.HoldsTraffic(st.BootstrapSent, st.WorldReady))
                continue;
            var payload = BuildServerChatPayload(text, st.ActorNr, live);
            sample ??= payload;
            if (SendCritical(p, payload))
                n++;
        }

        if (sample is not null && Diag.LogVerbose)
            CaptureSink.Capture("match_tx_ChatManager_server", sample);
        return n;
    }

    /// <summary>
    /// Кадр серверного чата: <c>field=1</c> (только его клиент рисует), автор — живой актор,
    /// не сам получатель, текст с пометкой сервера.
    /// </summary>
    private byte[] BuildServerChatPayload(
        string text, byte recipientActor, IReadOnlyCollection<byte> liveActors) =>
        MatchCodec.BuildChatManagerText(
            NextServerTime(),
            MatchChatAuthor.Sign(text),
            MatchChatAuthor.PickServerVoice(recipientActor, liveActors, MatchHostActor.ActorNr));

    /// <summary>
    /// У кого в комнате есть тело: таких авторов клиент рисует (буква C пробы /chattest).
    /// Основной источник — <c>SpawnedPawns</c>; режим, который его не ведёт, берётся из
    /// владельцев <c>LivingPawns</c> (тот же запасной путь, что у реконнекта).
    /// </summary>
    private static IReadOnlyCollection<byte> LiveChatActors(MatchRoom room)
    {
        if (room.SpawnedPawns.Count > 0)
            return room.SpawnedPawns.ToList();

        return room.LivingPawns.Values
            .Select(pawn => pawn.OwnerActorNr)
            .Where(actor => actor != 0)
            .Distinct()
            .ToList();
    }

    /// <summary>Сколько разных промахов доставки помним: журнал не должен расти бесконечно.</summary>
    private const int ChatMissLogCapacity = 64;

    private readonly object _chatMissGate = new();
    private readonly Dictionary<string, (DateTime At, string Reason)> _chatMissLog = new();

    /// <summary>
    /// Промах доставки в журнал — первый раз сразу, дальше только при смене причины или раз
    /// в <see cref="MatchChatDelivery.MissRepeatSeconds"/>: свипер отложенных ответов
    /// повторяет попытку каждую секунду (latest-3.log: 300 одинаковых строк).
    /// </summary>
    private void LogChatMiss(string key, string reason, string line)
    {
        var now = DateTime.UtcNow;
        lock (_chatMissGate)
        {
            if (_chatMissLog.Count > ChatMissLogCapacity)
                _chatMissLog.Clear();

            if (_chatMissLog.TryGetValue(key, out var last)
                && !MatchChatDelivery.ShouldLogMiss(last.At, now, last.Reason, reason))
                return;

            _chatMissLog[key] = (now, reason);
        }

        Console.WriteLine(line);
    }

    /// <summary>
    /// Диагностика матч-чата (<c>/chattest</c>): тот же ChatManager-кадр, но с явно заданными
    /// автором и <c>field</c>. Оба значения <c>field</c> (1 и 2) взяты из живых капчур —
    /// gold <c>20261112_040303_23056</c>/<c>…_040308_23079</c> идут с <c>field=1</c>,
    /// <c>…_040310_23085</c> «сучка» с <c>field=2</c>, payload одинаковый (байт длины + UTF-8).
    /// </summary>
    /// <remarks>
    /// Зачем: latest.log (2026-11-14, 3 пира) показал, что серверный кадр доходит
    /// (<c>peers=3</c>), но клиент его не рисует, и наоборот — сами клиенты ни одного Rpc на
    /// id=7 не прислали (80 Rpc, все на пешках, RadarManager id=6 и BombManager id=8). Значит
    /// чат клиента к нашему ChatManager не привязан, и выбирать между «автор должен быть живым
    /// игроком» и «нужен field=2» по капчурам нельзя — обе версии одинаково подтверждены.
    /// Прогон четырёх комбинаций на живом клиенте даёт недостающее наблюдение; в бой идёт
    /// только <see cref="SendServerChat"/>.
    /// </remarks>
    public int SendServerChatProbe(string text, byte authorActorNr, short field)
    {
        List<MatchRoom> rooms;
        lock (_roomGate)
            rooms = _roomsByPassword.Values.Distinct().ToList();

        var payload = MatchCodec.BuildChatManagerText(
            NextServerTime(), text, authorActorNr, field: field);
        var sent = 0;
        foreach (var room in rooms)
            sent += BroadcastInitReadyCritical(
                room, payload, tag: "match_tx_ChatManager_probe");
        return sent;
    }

    /// <summary>
    /// Аудитория серверного кадра прямо сейчас: только INIT-ready пиры вне world-gate
    /// получают чат, и отдельно — есть ли среди них заказчик (по нику ростера или адресу).
    /// </summary>
    /// <remarks>
    /// Зачем: проба, ушедшая в комнату без заказчика, не отвечает ни на один вопрос.
    /// latest-3.log — <c>/chattest</c> выполнен из лобби, пока собственный пир 7777 был
    /// отключён (<c>RemoteConnectionClose</c>), и четыре кадра ушли двум другим игрокам;
    /// заказчик не увидел ничего, а пять ответов ему истекли в очереди.
    /// </remarks>
    public string DescribeChatAudience(
        string? requesterNick,
        System.Net.IPAddress? requesterIp,
        out bool requesterPresent)
    {
        var target = ServerConfig.NormalizeNick(requesterNick);
        var audience = new List<string>();
        requesterPresent = false;

        foreach (var (p, st) in _peers)
        {
            if (!st.BootstrapSent)
                continue;
            if (MatchWorldGate.HoldsTraffic(st.BootstrapSent, st.WorldReady))
                continue;

            var nick = string.IsNullOrWhiteSpace(st.RosterName) ? "?" : st.RosterName;
            audience.Add($"{st.ActorNr}/{nick}@{p.Address}");

            var byNick = target.Length > 0
                && string.Equals(
                    ServerConfig.NormalizeNick(nick), target, StringComparison.OrdinalIgnoreCase);
            if (byNick || (requesterIp is not null && SameAddress(p.Address, requesterIp)))
                requesterPresent = true;
        }

        return audience.Count == 0
            ? "в катке никто не готов принимать чат"
            : string.Join(", ", audience);
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
    /// Never broadcasts — a private reply must not surface in the shared match chat — and
    /// refuses (returns 0) when the requester cannot be identified by address, so the caller
    /// queues the reply instead of showing somebody else's <c>/ids</c> to the wrong player.
    /// </summary>
    private int SendServerChatLastChance(string text, System.Net.IPAddress peerIp)
    {
        List<MatchRoom> rooms;
        lock (_roomGate)
            rooms = _roomsByPassword.Values.Distinct().ToList();
        foreach (var room in rooms)
        {
            var candidates = new List<(NetPeer Peer, MatchPeerState State)>();
            // Сколько пиров в комнате вообще: «готов один» и «в комнате один» — разные вещи,
            // и именно их путала прежняя эвристика (см. MatchChatDelivery.LastChanceMaySend).
            var inRoom = 0;
            foreach (var (p, st) in _peers)
            {
                if (st.Room != room)
                    continue;
                inRoom++;
                if (!st.BootstrapSent)
                    continue;
                candidates.Add((p, st));
            }
            if (candidates.Count == 0)
                continue;
            // Prefer the peer that came from the same machine as the lobby requester.
            var sameIp = candidates
                .Where(c => SameAddress(c.Peer.Address, peerIp))
                .ToList();

            // Совпадения по адресу нет — угадывать нельзя: приватный ответ уйдёт чужому.
            // «Готовый пир всего один» больше не аргумент: latest-2.log:223 — ответ для
            // «влал» (192.168.1.70, actor 3, bootstrap ещё не отправлен) ушёл «влал2»
            // (192.168.1.65, actor 2) только потому, что тот успел первым. Ноль доставок —
            // вызывающий положит ответ в очередь, и свипер досылает его уже по нику.
            if (!MatchChatDelivery.LastChanceMaySend(sameIp.Count, inRoom))
            {
                // Свипер повторяет попытку раз в секунду, поэтому пишем не каждый раз
                // (latest-3.log: 300 одинаковых отказов на пять отложенных ответов).
                LogChatMiss(
                    $"lastchance:{peerIp}:{FirstLine(text)}",
                    $"готовых={candidates.Count} комната={inRoom}",
                    $"[chat] last-chance: ip={peerIp} не совпал ни с кем из {candidates.Count} " +
                    $"готовых (в комнате {inRoom}) — приватный ответ не отправляю: " +
                    "он ушёл бы чужому, ждём адресата в очереди");
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
            // Автор — живой актор и не сам получатель: кадр от актора сервера клиент не рисует.
            var payload = BuildServerChatPayload(text, target.ActorNr, LiveChatActors(room));
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

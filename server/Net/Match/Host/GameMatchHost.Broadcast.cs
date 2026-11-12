using System.Buffers.Binary;
using System.Net;
using LiteNetLib;
using StandChillow.LanServer.Diagnostics;
using StandChillow.LanServer.Lan;
using StandChillow.LanServer.Net.Lobby;
using StandChillow.LanServer.Net.Match;
using StandChillow.LanServer.Net.Match.Host;

namespace StandChillow.LanServer.Net;

public sealed partial class GameMatchHost
{
    /// <summary>
    /// Сколько надёжных пакетов терпим в очереди LiteNetLib на одного пира, прежде чем
    /// считать его «отстающим». Дальше ему шлём только критичное, а <b>косметический</b>
    /// поток (WorldObjectState, спавн-позы, лёгкие Rpc) — дропаем: лучше пропустить тик,
    /// чем копить очередь, которая растёт быстрее, чем клиент успевает вычитывать.
    /// </summary>
    /// <remarks>
    /// <b>Никогда не дропаем</b> структурные надёжные пакеты: CreateWorldObject /
    /// DestroyWorldObject / SetProperties / ActorJoined+Left / C2-фазы / handshake /
    /// JoinRoom. Пропуск Destroy или CWO — это ровно те «object reference» на клиенте,
    /// которых мы не хотим. Такие пакеты уходят всегда, даже в переполненную очередь:
    /// их немного, и они задают состояние мира.
    /// </remarks>
    private const int ReliableQueueSoftLimit = 256;

    /// <summary>Какие надёжные пакеты можно дропнуть отстающему пиру (только косметика/позы).</summary>
    internal static bool IsDroppableWhenBacklogged(MatchOpcode op) => op switch
    {
        // Поза/состояние мира — следующий тик всё равно перезапишет.
        MatchOpcode.WorldObjectState => true,
        // Rpc-поток спавнов/эффектов: устаревает на следующем кадре.
        MatchOpcode.WorldObjectRpc => true,
        // Create/Destroy/SetProperty(-ies), ActorJoined/Left, CustomEvent, handshake,
        // JoinRoom, фазы (C2 идут как SetProperty) — структурные, НЕ дропаем.
        _ => false,
    };

    /// <summary>Опкод надёжного пакета (для решения о дропе); не распознан → не дропаем.</summary>
    private bool TryGetReliableOpcode(byte[] payload, out MatchOpcode op)
    {
        op = default;
        try
        {
            return MatchCodec.TryParseEnvelope(
                payload, out _, out op, out _, out _);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Пир помечается «медленным», если отправка не удалась или очередь переполнена.</summary>
    private static bool IsBacklogged(NetPeer peer, int softLimit = ReliableQueueSoftLimit)
    {
        try
        {
            return peer.GetPacketsCountInReliableQueue(0, true) >= softLimit;
        }
        catch
        {
            return false;
        }
    }

    private int BroadcastRoom(MatchRoom room, byte[] payload, string? tag)
    {
        var n = 0;
        foreach (var (p, st) in _peers)
        {
            if (st.Room != room)
                continue;
            if (Send(p, payload))
                n++;
        }
        if (tag is not null && Diag.LogVerbose)
            CaptureSink.Capture(tag!, payload);
        return n;
    }

    /// <summary>Reliable to room peers except one (ActorJoined excludes the joiner).</summary>
    private int BroadcastRoomExcept(MatchRoom room, NetPeer except, byte[] payload, string? tag)
    {
        var n = 0;
        foreach (var (p, st) in _peers)
        {
            if (st.Room != room || ReferenceEquals(p, except))
                continue;
            if (Send(p, payload))
                n++;
        }
        if (tag is not null && Diag.LogVerbose)
            CaptureSink.Capture(tag!, payload);
        return n;
    }

    /// <summary>
    /// Reliable to peers who finished INIT (BootstrapSent). Pawn CWO / rpc go here so
    /// late joiners do not get a live CWO and then a second copy from living-pawn snapshot.
    /// </summary>
    private int BroadcastInitReady(MatchRoom room, byte[] payload, string? tag) =>
        BroadcastInitReadyExcept(room, except: null, payload, tag, capture: true);

    /// <summary>
    /// INIT-ready relay excluding sender — client already applied locally (CWO/Rpc/Destroy).
    /// </summary>
    private int BroadcastInitReadyExcept(
        MatchRoom room,
        NetPeer? except,
        byte[] payload,
        string? tag,
        bool capture = true)
    {
        var n = 0;
        foreach (var (p, st) in _peers)
        {
            if (st.Room != room || !st.BootstrapSent)
                continue;
            if (except is not null && ReferenceEquals(p, except))
                continue;
            if (Send(p, payload))
                n++;
        }
        if (capture && tag is not null && Diag.LogVerbose)
            CaptureSink.Capture(tag!, payload);
        return n;
    }

    private int BroadcastInitReadyUnreliable(MatchRoom room, byte[] payload, string? tag)
    {
        var n = 0;
        foreach (var (p, st) in _peers)
        {
            if (st.Room != room || !st.BootstrapSent)
                continue;
            if (SendUnreliable(p, payload))
                n++;
        }
        if (tag is not null && Diag.LogVerbose)
            CaptureSink.Capture(tag!, payload);
        return n;
    }

    /// <summary>Unreliable relay to INIT-ready match peers except the sender.</summary>
    private int BroadcastInitReadyUnreliableExcept(MatchRoom room, NetPeer except, byte[] payload)
    {
        var n = 0;
        foreach (var (p, st) in _peers)
        {
            if (st.Room != room || !st.BootstrapSent || ReferenceEquals(p, except))
                continue;
            if (SendUnreliable(p, payload))
                n++;
        }
        return n;
    }

    private void SendAndDump(NetPeer peer, byte[] payload)
    {
        Send(peer, payload);
        if (!Diag.LogVerbose)
            return;
        if (!MatchCodec.TryParseEnvelope(payload, out _, out var op, out _, out _))
        {
            CaptureSink.Capture("match_tx_world", payload);
            return;
        }
        CaptureSink.Capture($"match_tx_{MatchCodec.OpcodeName(op)}", payload);
    }

    private void EnsureRoom(string passwordKey, string roomId)
    {
        lock (_roomGate)
        {
            if (_roomsByPassword.ContainsKey(passwordKey))
                return;
            _roomsByPassword[passwordKey] = CreateRoomWithServerHost(passwordKey, roomId);
            Console.WriteLine(
                $"[match-host] CreateRoom passwordKey={(passwordKey.Length == 0 ? "\"\"" : $"'{passwordKey}'")} " +
                $"roomId='{roomId}' hostActor={MatchHostActor.ActorNr}/'{MatchHostActor.Name}'");
        }
    }

    private int NextServerTime()
    {
        // Phone host advances serverTime with real clock (capture deltas ~ms).
        // Increment-only seed breaks Time/RoundStartTime deadlines for the match UI.
        var now = Environment.TickCount;
        while (true)
        {
            var prev = Volatile.Read(ref _serverTimeSeed);
            var next = now <= prev ? prev + 1 : now;
            if (Interlocked.CompareExchange(ref _serverTimeSeed, next, prev) == prev)
                return next;
        }
    }

    /// <summary>Server-time seconds — same unit as room <c>Time</c>/<c>RoundStartTime</c>
    /// and WorldObjectRpc timeValue. Client <c>dws.bfwi</c> / <c>NetManager.bfqt</c> =
    /// <c>(TickCount + offset) / 1000</c>. FetchServerTime header stays raw TickCount ms
    /// (<c>fyf.bodw</c>); do not put seconds into the HasServerTime i32.</summary>
    private double ServerTimeSeconds() => NextServerTime() / 1000.0;

    /// <summary>
    /// Reliable send. Returns <c>false</c> when the peer's reliable queue is full
    /// (packet dropped on purpose — see <see cref="ReliableQueueSoftLimit"/>) or the send
    /// failed. Dropping a game tick for one slow phone is far cheaper than letting its
    /// queue grow until the whole host stalls.
    /// </summary>
    private bool Send(NetPeer peer, byte[] payload)
    {
        // Backpressure only for droppable (cosmetic) traffic. Structural packets always go,
        // otherwise a slow queue would cause client-side desync / object references.
        if (IsBacklogged(peer) && TryGetReliableOpcode(payload, out var op)
            && IsDroppableWhenBacklogged(op))
        {
            Interlocked.Increment(ref _droppedSends);
            return false;
        }

        try
        {
            // gcv.Reliable → LiteNetLib ReliableOrdered (same as client match TX).
            // Note: NetPeer.Send returns void in LiteNetLib 2.1.5, so success is "did not throw".
            peer.Send(payload, DeliveryMethod.ReliableOrdered);
            return true;
        }
        catch (Exception ex)
        {
            Interlocked.Increment(ref _droppedSends);
            Console.WriteLine($"[match-host] send failed to {peer.Address}: {ex.Message}");
            return false;
        }
    }

    /// <summary>Unreliable send; never throws (UDP — a lost state tick is expected).</summary>
    private bool SendUnreliable(NetPeer peer, byte[] payload)
    {
        try
        {
            // WorldObjectState phone path: Unreliable + flags=None.
            peer.Send(payload, DeliveryMethod.Unreliable);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private void DumpCapture(string tag, byte[] payload) => CaptureSink.Capture(tag, payload);
}

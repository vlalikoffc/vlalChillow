using System.Buffers.Binary;
using System.Net;
using LiteNetLib;
using StandChillow.LanServer.Lan;
using StandChillow.LanServer.Net.Lobby;
using StandChillow.LanServer.Net.Match;
using StandChillow.LanServer.Net.Match.Host;

namespace StandChillow.LanServer.Net;

public sealed partial class GameMatchHost
{
    private int BroadcastRoom(MatchRoom room, byte[] payload, string tag)
    {
        var n = 0;
        foreach (var (p, st) in _peers)
        {
            if (st.Room == room)
            {
                Send(p, payload);
                n++;
            }
        }
        DumpCapture(tag, payload);
        return n;
    }

    /// <summary>Reliable to room peers except one (ActorJoined excludes the joiner).</summary>
    private int BroadcastRoomExcept(MatchRoom room, NetPeer except, byte[] payload, string tag)
    {
        var n = 0;
        foreach (var (p, st) in _peers)
        {
            if (st.Room == room && !ReferenceEquals(p, except))
            {
                Send(p, payload);
                n++;
            }
        }
        DumpCapture(tag, payload);
        return n;
    }

    /// <summary>
    /// Reliable to peers who finished INIT (BootstrapSent). Pawn CWO / rpc go here so
    /// late joiners do not get a live CWO and then a second copy from living-pawn snapshot.
    /// </summary>
    private int BroadcastInitReady(MatchRoom room, byte[] payload, string tag) =>
        BroadcastInitReadyExcept(room, except: null, payload, tag, dumpCapture: true);

    /// <summary>
    /// INIT-ready relay excluding sender — client already applied locally (CWO/Rpc/Destroy).
    /// </summary>
    private int BroadcastInitReadyExcept(
        MatchRoom room,
        NetPeer? except,
        byte[] payload,
        string? tag,
        bool dumpCapture = true)
    {
        var n = 0;
        foreach (var (p, st) in _peers)
        {
            if (st.Room != room || !st.BootstrapSent)
                continue;
            if (except is not null && ReferenceEquals(p, except))
                continue;
            Send(p, payload);
            n++;
        }
        if (dumpCapture && tag is not null)
            DumpCapture(tag, payload);
        return n;
    }

    private int BroadcastInitReadyUnreliable(MatchRoom room, byte[] payload, string? tag)
    {
        var n = 0;
        foreach (var (p, st) in _peers)
        {
            if (st.Room == room && st.BootstrapSent)
            {
                SendUnreliable(p, payload);
                n++;
            }
        }
        if (tag is not null)
            DumpCapture(tag, payload);
        return n;
    }

    /// <summary>Unreliable relay to INIT-ready match peers except the sender.</summary>
    private int BroadcastInitReadyUnreliableExcept(MatchRoom room, NetPeer except, byte[] payload)
    {
        var n = 0;
        foreach (var (p, st) in _peers)
        {
            if (st.Room == room && st.BootstrapSent && !ReferenceEquals(p, except))
            {
                SendUnreliable(p, payload);
                n++;
            }
        }
        return n;
    }

    private void SendAndDump(NetPeer peer, byte[] payload)
    {
        Send(peer, payload);
        if (!MatchCodec.TryParseEnvelope(payload, out _, out var op, out _, out _))
        {
            DumpCapture("match_tx_world", payload);
            return;
        }
        DumpCapture($"match_tx_{MatchCodec.OpcodeName(op)}", payload);
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

    private void Send(NetPeer peer, byte[] payload)
    {
        // gcv.Reliable → LiteNetLib ReliableOrdered (same as client match TX).
        peer.Send(payload, DeliveryMethod.ReliableOrdered);
    }

    private static void SendUnreliable(NetPeer peer, byte[] payload)
    {
        // WorldObjectState phone path: Unreliable + flags=None.
        peer.Send(payload, DeliveryMethod.Unreliable);
    }

    private void DumpCapture(string tag, byte[] payload)
    {
        try
        {
            var i = Interlocked.Increment(ref _captureIndex);
            var path = Path.Combine(
                _captureDir,
                $"{DateTime.UtcNow:yyyyMMdd_HHmmss}_{i:D3}_{tag}_len{payload.Length}.bin");
            File.WriteAllBytes(path, payload);
            Console.WriteLine($"[match-host] captured {path}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[match-host] capture failed: {ex.Message}");
        }
    }
}

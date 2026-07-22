using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;

namespace StandChillow.LanServer.Lan;

/// <summary>
/// Chillow NetLanDiscovery host visibility stack.
/// Protocol (DummyDLL + ISIL + live phone RX):
///   - UDP 5056 (gpe.cycf) = discovery ONLY (probe / LanDiscoveryInfo reply). NOT the match channel.
///   - UDP 7778 (LanLobbyHelper.cbtb / qxg) = LiteNetLib lobby join, advertised inside LanDiscoveryInfo.
///   - UDP 7777 = LiteNetLib game/match after OpGameHostingStateChangedEvent (live esz port) — not discovery.
///   - Phone create-lobby: LanLobbyController → gpe.bqkg (listen 5056) + qxt (payload) + lobby on 7778.
///   - Phone search UI: LanDiscoveryPopupController → gpe.bqkb (broadcast probes); searchers do NOT reply.
///   - Live: searching phone → RX PROBE on our 5056 → TX reply → lobby listed; join then hits 7778.
///   - Probe magics (exact body → same LanDiscoveryInfo unicast reply):
///       * "StandChillow LAN Bonjour V2" — live phone (metadata lit)
///       * "sosalbolt482" — DummyDLL const gpe.cyce (legacy/alt)
///   - Proto byte = LanLobbyHelper.cbta = 4
/// Hosts do NOT unsolicited-broadcast (KeepAlive only calls qxt to refresh payload).
/// Not Apple mDNS/DNS-SD — "Bonjour" is only the probe UTF-8 name.
/// </summary>
public sealed class LanDiscoveryHost : IAsyncDisposable
{
    public const int DiscoveryPort = 5056;       // gpe.cycf
    public const ushort DefaultGamePort = 7778;  // LanLobbyHelper.cbtb / qxg → 0x1E62
    public const byte ProtocolVersion = 4;       // LanLobbyHelper.cbta

    /// <summary>Live phone probe (global-metadata string literal, len=27).</summary>
    public const string ProbeBonjourV2 = "StandChillow LAN Bonjour V2";
    /// <summary>DummyDLL const gpe.cyce — alternate/legacy magic.</summary>
    public const string ProbeSosalbolt = "sosalbolt482";

    public static readonly byte[] ProbeMagicBonjourV2 = Encoding.UTF8.GetBytes(ProbeBonjourV2);
    public static readonly byte[] ProbeMagicSosalbolt = Encoding.UTF8.GetBytes(ProbeSosalbolt);
    /// <summary>Preferred name for logs; both magics are accepted.</summary>
    public static readonly byte[] ProbeMagic = ProbeMagicBonjourV2;

    private readonly CancellationTokenSource _cts = new();
    private readonly object _gate = new();
    private readonly ConcurrentDictionary<string, byte> _recentProbeKeys = new();
    private readonly List<Task> _tasks = new();

    private UdpClient? _rx;
    private readonly List<(LanEndpoint Lan, Socket Tx)> _txByNic = new();
    private byte[] _payload;
    private IReadOnlyList<LanEndpoint> _endpoints = Array.Empty<LanEndpoint>();

    private long _datagrams;
    private long _probes;
    private long _replies;
    private long _replyErrors;
    private long _deduped;

    /// <summary>When true, reply from every LAN NIC (visibility over uniqueness).</summary>
    public bool ReplyFromAllLanNics { get; set; } = true;

    public LanDiscoveryHost(LanDiscoveryInfo info)
    {
        _payload = info.Serialize();
    }

    public IReadOnlyList<LanEndpoint> BoundEndpoints
    {
        get { lock (_gate) return _endpoints; }
    }

    public long DatagramsReceived => Interlocked.Read(ref _datagrams);
    public long ProbesMatched => Interlocked.Read(ref _probes);
    public long RepliesSent => Interlocked.Read(ref _replies);
    public long ReplyErrors => Interlocked.Read(ref _replyErrors);
    public long ProbesDeduped => Interlocked.Read(ref _deduped);

    public void UpdateInfo(LanDiscoveryInfo info)
    {
        var bytes = info.Serialize();
        lock (_gate) _payload = bytes;
        Console.WriteLine($"[discovery] payload updated len={bytes.Length} hex={Convert.ToHexString(bytes)}");
    }

    public void Start()
    {
        RebuildBinds(forceLog: true);
        NetworkChange.NetworkAddressChanged += OnNetworkChanged;
        NetworkChange.NetworkAvailabilityChanged += OnNetworkAvailabilityChanged;

        _tasks.Add(Task.Run(() => ReceiveLoopAsync(_cts.Token)));
        _tasks.Add(Task.Run(() => NicWatchLoopAsync(_cts.Token)));
        _tasks.Add(Task.Run(() => StatusLoopAsync(_cts.Token)));
    }

    private void OnNetworkChanged(object? sender, EventArgs e) =>
        QueueRebuild("NetworkAddressChanged");

    private void OnNetworkAvailabilityChanged(object? sender, NetworkAvailabilityEventArgs e) =>
        QueueRebuild($"NetworkAvailability isAvailable={e.IsAvailable}");

    private void QueueRebuild(string reason)
    {
        Console.WriteLine($"[discovery] network event: {reason} — rebuilding binds");
        try { RebuildBinds(forceLog: true); }
        catch (Exception ex) { Console.WriteLine($"[discovery] rebuild failed: {ex.Message}"); }
    }

    private void RebuildBinds(bool forceLog)
    {
        var endpoints = LanInterfacePicker.GetLanIpv4Endpoints();
        if (endpoints.Count == 0)
            throw new InvalidOperationException("No LAN IPv4 interfaces (VPN-only?). Connect Wi‑Fi/Ethernet.");

        lock (_gate)
        {
            // Tear down old TX first so we can re-bind cleanly.
            foreach (var (_, sock) in _txByNic)
            {
                try { sock.Dispose(); } catch { /* ignore */ }
            }
            _txByNic.Clear();

            _rx?.Dispose();
            _rx = CreateReceiveSocket();

            foreach (var ep in endpoints)
            {
                var tx = CreateReplySocket(ep.Address);
                _txByNic.Add((ep, tx));
            }

            _endpoints = endpoints;
        }

        if (forceLog)
            LogBindMap();
    }

    private void LogBindMap()
    {
        Console.WriteLine("[discovery] === bind map ===");
        Console.WriteLine($"[discovery]   RX  0.0.0.0:{DiscoveryPort}  (broadcasts + unicasts)");
        foreach (var (lan, sock) in _txByNic)
        {
            var local = (IPEndPoint)sock.LocalEndPoint!;
            Console.WriteLine(
                $"[discovery]   TX  {lan.InterfaceName} {lan.Address}/{PrefixLen(lan.SubnetMask)} " +
                $"br={lan.Broadcast}  local={local.Address}:{local.Port}");
        }

        foreach (var skipped in LanInterfacePicker.DescribeSkippedInterfaces())
            Console.WriteLine($"[discovery]   skip {skipped}");
        Console.WriteLine("[discovery] === end bind map ===");
    }

    private static int PrefixLen(IPAddress mask)
    {
        var b = mask.GetAddressBytes();
        var bits = 0;
        foreach (var x in b)
        {
            for (var i = 7; i >= 0; i--)
            {
                if (((x >> i) & 1) == 0) return bits;
                bits++;
            }
        }
        return bits;
    }

    /// <summary>
    /// Wildcard RX — required on Linux so subnet broadcasts are delivered.
    /// Do NOT also bind NIC:5056 for TX (that steals unicast away from wildcard).
    /// </summary>
    private static UdpClient CreateReceiveSocket()
    {
        var udp = new UdpClient(AddressFamily.InterNetwork);
        udp.ExclusiveAddressUse = false;
        udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        try
        {
            // Prefer receiving broadcasts even under odd firewall/sysctl setups.
            udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.Broadcast, true);
        }
        catch { /* ignore */ }
        udp.EnableBroadcast = true;
        udp.Client.Bind(new IPEndPoint(IPAddress.Any, DiscoveryPort));
        return udp;
    }

    /// <summary>
    /// Per-NIC TX bound to LAN unicast + ephemeral port so reply source IP is the
    /// reachable Wi‑Fi/Ethernet address even when VPN owns the default route.
    /// Ephemeral port avoids stealing unicast probes from the wildcard RX socket.
    /// </summary>
    private static Socket CreateReplySocket(IPAddress lanAddress)
    {
        var sock = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        sock.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        sock.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.Broadcast, true);
        sock.Bind(new IPEndPoint(lanAddress, 0));
        TryBindToDevice(sock, lanAddress);
        return sock;
    }

    private static void TryBindToDevice(Socket sock, IPAddress lanAddress)
    {
        // Linux SO_BINDTODEVICE = 25 — pin TX to the physical NIC when possible.
        if (!OperatingSystem.IsLinux()) return;
        var name = LanInterfacePicker.FindInterfaceName(lanAddress);
        if (string.IsNullOrEmpty(name)) return;
        try
        {
            var bytes = Encoding.ASCII.GetBytes(name + "\0");
            sock.SetRawSocketOption((int)SocketOptionLevel.Socket, 25, bytes);
            Console.WriteLine($"[discovery] SO_BINDTODEVICE {name} for {lanAddress}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[discovery] SO_BINDTODEVICE {name} skipped: {ex.Message}");
        }
    }

    private async Task ReceiveLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            UdpClient? rx;
            lock (_gate) rx = _rx;
            if (rx is null)
            {
                await Task.Delay(100, ct);
                continue;
            }

            try
            {
                var result = await rx.ReceiveAsync(ct);
                await HandleDatagramAsync(result.Buffer, result.RemoteEndPoint, ct);
            }
            catch (OperationCanceledException) { break; }
            catch (ObjectDisposedException)
            {
                await Task.Delay(50, ct);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[discovery] RX error: {ex.Message}");
                await Task.Delay(200, ct);
            }
        }
    }

    private async Task HandleDatagramAsync(byte[] buf, IPEndPoint from, CancellationToken ct)
    {
        Interlocked.Increment(ref _datagrams);
        var probeKind = MatchProbe(buf);
        var hex = Convert.ToHexString(buf);
        var ascii = PreviewAscii(buf);

        if (probeKind is null)
        {
            Console.WriteLine(
                $"[discovery] RX non-probe from {from} len={buf.Length} hex={hex} ASCII={ascii}");
            return;
        }

        // Dedup identical probes arriving within a short window (client can flood).
        var dedupeKey = $"{from.Address}:{from.Port}";
        if (!_recentProbeKeys.TryAdd(dedupeKey, 0))
        {
            Interlocked.Increment(ref _deduped);
            Console.WriteLine(
                $"[discovery] RX PROBE kind={probeKind} from {from} (deduped, still will reply)");
            // Still reply — aggressive visibility. Only skip logging spam via counter.
        }
        else
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(250, ct);
                    _recentProbeKeys.TryRemove(dedupeKey, out _);
                }
                catch { /* ignore */ }
            }, ct);
        }

        Interlocked.Increment(ref _probes);
        Console.WriteLine(
            $"[discovery] RX PROBE kind={probeKind} from {from} len={buf.Length} hex={hex}");

        byte[] payload;
        List<(LanEndpoint Lan, Socket Tx)> targets;
        lock (_gate)
        {
            payload = _payload;
            targets = SelectTxSockets(from);
        }

        if (targets.Count == 0)
        {
            Console.WriteLine($"[discovery] TX FAIL no LAN reply socket for {from}");
            Interlocked.Increment(ref _replyErrors);
            return;
        }

        foreach (var (lan, sock) in targets)
        {
            try
            {
                var sent = await sock.SendToAsync(payload, SocketFlags.None, from, ct);
                Interlocked.Increment(ref _replies);
                var local = (IPEndPoint)sock.LocalEndPoint!;
                Console.WriteLine(
                    $"[discovery] TX reply → {from}  {sent}B  src={local.Address}:{local.Port} " +
                    $"via {lan.InterfaceName}  payloadHex={Convert.ToHexString(payload)}");
            }
            catch (Exception ex)
            {
                Interlocked.Increment(ref _replyErrors);
                Console.WriteLine($"[discovery] TX FAIL → {from} via {lan.InterfaceName}: {ex.Message}");
            }
        }
    }

    /// <summary>Exact-body probe match. Returns kind name or null.</summary>
    private static string? MatchProbe(ReadOnlySpan<byte> buf)
    {
        if (buf.SequenceEqual(ProbeMagicBonjourV2)) return "bonjour-v2";
        if (buf.SequenceEqual(ProbeMagicSosalbolt)) return "sosalbolt482";
        return null;
    }

    private static string PreviewAscii(ReadOnlySpan<byte> buf)
    {
        Span<char> chars = stackalloc char[Math.Min(buf.Length, 64)];
        for (var i = 0; i < chars.Length; i++)
        {
            var b = buf[i];
            chars[i] = b is >= 32 and <= 126 ? (char)b : '.';
        }
        return new string(chars);
    }

    private List<(LanEndpoint Lan, Socket Tx)> SelectTxSockets(IPEndPoint remote)
    {
        var matched = _txByNic
            .Where(t => SameSubnet(t.Lan.Address, t.Lan.SubnetMask, remote.Address))
            .ToList();

        if (ReplyFromAllLanNics)
        {
            // Prefer matched first, then any remaining NICs (phone may be on AP guest quirks).
            var rest = _txByNic.Where(t => !matched.Contains(t));
            return matched.Concat(rest).ToList();
        }

        if (matched.Count > 0)
            return matched;
        return _txByNic.ToList();
    }

    private static bool SameSubnet(IPAddress a, IPAddress mask, IPAddress b)
    {
        var aa = a.GetAddressBytes();
        var mm = mask.GetAddressBytes();
        var bb = b.GetAddressBytes();
        if (aa.Length != 4 || bb.Length != 4 || mm.Length != 4) return false;
        for (var i = 0; i < 4; i++)
        {
            if ((aa[i] & mm[i]) != (bb[i] & mm[i]))
                return false;
        }
        return true;
    }

    private async Task NicWatchLoopAsync(CancellationToken ct)
    {
        var last = string.Join("|", BoundEndpoints.Select(e => e.Address.ToString()));
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(5), ct);
                var now = string.Join("|", LanInterfacePicker.GetLanIpv4Endpoints().Select(e => e.Address.ToString()));
                if (now != last)
                {
                    last = now;
                    QueueRebuild("NIC inventory changed");
                }
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                Console.WriteLine($"[discovery] nic-watch: {ex.Message}");
            }
        }
    }

    private async Task StatusLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(15), ct);
                var nics = string.Join(", ", BoundEndpoints.Select(e => $"{e.InterfaceName}/{e.Address}"));
                Console.WriteLine(
                    $"[discovery] heartbeat dg={DatagramsReceived} probes={ProbesMatched} " +
                    $"replies={RepliesSent} err={ReplyErrors} dedupe={ProbesDeduped} nics=[{nics}]");
                if (DatagramsReceived == 0)
                    Console.WriteLine(
                        "[discovery] tip: 0 datagrams so far — if phone is searching, packets never reach this PC " +
                        "(AP client isolation / wrong Wi‑Fi / phone VPN).");
            }
            catch (OperationCanceledException) { break; }
        }
    }

    public async ValueTask DisposeAsync()
    {
        NetworkChange.NetworkAddressChanged -= OnNetworkChanged;
        NetworkChange.NetworkAvailabilityChanged -= OnNetworkAvailabilityChanged;
        _cts.Cancel();
        try { await Task.WhenAll(_tasks); } catch { /* ignore */ }
        lock (_gate)
        {
            _rx?.Dispose();
            foreach (var (_, s) in _txByNic) s.Dispose();
            _txByNic.Clear();
        }
        _cts.Dispose();
    }
}

public sealed record LanDiscoveryInfo
{
    public string HostName { get; init; } = "Server";
    public string LevelName { get; init; } = "влал хостит рялна";
    public byte MemberCount { get; init; } = 1;
    public byte ProtocolVersion { get; init; } = LanDiscoveryHost.ProtocolVersion;
    public ushort GamePort { get; init; } = LanDiscoveryHost.DefaultGamePort;
    /// <summary>
    /// gbe.cxbl — when true, cxbm/cxbn follow (waiting lobby: level + GameModeId for map UI).
    /// When false, extras omitted → Join / GameAlreadyStarted details (in-progress).
    /// </summary>
    public bool HasExtraStrings { get; init; }
    public string Extra1 { get; init; } = "";
    public string Extra2 { get; init; } = "";

    public byte[] Serialize()
    {
        // Order matches gbe.mur → fzr.bojm/bojd/boje (LE ushort).
        var ms = new MemoryStream(64);
        WriteString(ms, HostName);                 // cxbg
        WriteString(ms, LevelName);                // cxbh
        ms.WriteByte(MemberCount);                 // cxbi
        ms.WriteByte(ProtocolVersion);             // cxbj must be 4
        Span<byte> u16 = stackalloc byte[2];
        BinaryPrimitives.WriteUInt16LittleEndian(u16, GamePort); // cxbk
        ms.Write(u16);
        ms.WriteByte(HasExtraStrings ? (byte)1 : (byte)0); // cxbl
        if (HasExtraStrings)
        {
            WriteString(ms, Extra1); // cxbm
            WriteString(ms, Extra2); // cxbn
        }
        return ms.ToArray();
    }

    public string ToHex() => Convert.ToHexString(Serialize());

    public static bool TryDeserialize(ReadOnlySpan<byte> data, out LanDiscoveryInfo info)
    {
        info = default!;
        try
        {
            var r = new DiscoveryReader(data);
            var host = r.ReadString();
            var level = r.ReadString();
            var members = r.ReadByte();
            var proto = r.ReadByte();
            var port = r.ReadUInt16();
            var hasExtra = r.ReadByte() != 0;
            string extra1 = "", extra2 = "";
            if (hasExtra)
            {
                extra1 = r.ReadString();
                extra2 = r.ReadString();
            }
            // Trailing bytes are unusual but tolerate (don't invent fields).
            info = new LanDiscoveryInfo
            {
                HostName = host,
                LevelName = level,
                MemberCount = members,
                ProtocolVersion = proto,
                GamePort = port,
                HasExtraStrings = hasExtra,
                Extra1 = extra1,
                Extra2 = extra2,
            };
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static void WriteString(Stream s, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value ?? "");
        WriteVarInt(s, bytes.Length);
        s.Write(bytes);
    }

    private static void WriteVarInt(Stream s, int value)
    {
        var v = (uint)value;
        while (v >= 0x80)
        {
            s.WriteByte((byte)(v | 0x80));
            v >>= 7;
        }
        s.WriteByte((byte)v);
    }

    private ref struct DiscoveryReader
    {
        private readonly ReadOnlySpan<byte> _data;
        private int _pos;

        public DiscoveryReader(ReadOnlySpan<byte> data)
        {
            _data = data;
            _pos = 0;
        }

        private int Remaining => _data.Length - _pos;

        public byte ReadByte()
        {
            if (Remaining < 1) throw new InvalidDataException("need byte");
            return _data[_pos++];
        }

        public ushort ReadUInt16()
        {
            if (Remaining < 2) throw new InvalidDataException("need u16");
            var v = BinaryPrimitives.ReadUInt16LittleEndian(_data.Slice(_pos));
            _pos += 2;
            return v;
        }

        public string ReadString()
        {
            var len = ReadVarInt();
            if (len < 0 || len > Remaining) throw new InvalidDataException("bad string");
            var s = Encoding.UTF8.GetString(_data.Slice(_pos, len));
            _pos += len;
            return s;
        }

        private int ReadVarInt()
        {
            var result = 0;
            var shift = 0;
            while (true)
            {
                if (Remaining < 1) throw new InvalidDataException("bad varint");
                var b = _data[_pos++];
                result |= (b & 0x7F) << shift;
                if ((b & 0x80) == 0) return result;
                shift += 7;
                if (shift > 35) throw new InvalidDataException("varint too long");
            }
        }
    }
}

/// <summary>
/// Discovery client (ConnectAsClient): broadcast Bonjour V2 (+ sosalbolt482) on UDP 5056 from LAN NICs,
/// collect LanDiscoveryInfo unicast replies, then join advertised GamePort (7778).
/// Matches phone search stack (gpe.bqkb → ephemeral UdpClient + broadcast), not host listen (bqkg).
/// Learning only — never for fake loopback self-test.
/// </summary>
public sealed class LanDiscoveryClient : IAsyncDisposable
{
    private static readonly IPAddress GlobalBroadcast = IPAddress.Parse("255.255.255.255");

    private readonly CancellationTokenSource _cts = new();
    private readonly object _gate = new();
    private readonly Dictionary<string, DiscoveredLobby> _lobbies = new(StringComparer.Ordinal);
    private readonly List<Socket> _sockets = new();
    private readonly List<Task> _rxTasks = new();
    private Task? _probeTask;
    private long _probesSent;
    private long _datagrams;
    private long _echoes;
    private long _parseFails;
    private long _replies;
    private int _probeRoundLogged;

    public IReadOnlyList<DiscoveredLobby> Snapshot()
    {
        lock (_gate) return _lobbies.Values.OrderBy(l => l.FirstSeenUtc).ToList();
    }

    public long ProbesSent => Interlocked.Read(ref _probesSent);
    public long DatagramsReceived => Interlocked.Read(ref _datagrams);
    public long EchoesIgnored => Interlocked.Read(ref _echoes);
    public long ParseFailures => Interlocked.Read(ref _parseFails);
    public long RepliesReceived => Interlocked.Read(ref _replies);

    public void Start()
    {
        var endpoints = LanInterfacePicker.GetLanIpv4Endpoints();
        if (endpoints.Count == 0)
            throw new InvalidOperationException("No LAN IPv4 interfaces for discovery client.");

        Console.WriteLine(
            $"[client-disc] probe UDP {LanDiscoveryHost.DiscoveryPort} " +
            $"(discovery only; join uses LanDiscoveryInfo.GamePort={LanDiscoveryHost.DefaultGamePort})");
        Console.WriteLine(
            $"[client-disc] magics: '{LanDiscoveryHost.ProbeBonjourV2}' + '{LanDiscoveryHost.ProbeSosalbolt}'");

        foreach (var ep in endpoints)
        {
            var sock = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            sock.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            sock.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.Broadcast, true);
            // Phone search uses UdpClient(0) — ephemeral local port; host replies unicast to that port.
            sock.Bind(new IPEndPoint(ep.Address, 0));
            _sockets.Add(sock);
            var local = (IPEndPoint)sock.LocalEndPoint!;
            Console.WriteLine(
                $"[client-disc] TX/RX bind {ep.InterfaceName} {local.Address}:{local.Port} " +
                $"subnet-br={ep.Broadcast} + {GlobalBroadcast}");
            _rxTasks.Add(Task.Run(() => ReceiveOneSocketAsync(sock, ct: _cts.Token)));
        }

        _probeTask = Task.Run(() => ProbeLoopAsync(_cts.Token));
    }

    public async Task<IReadOnlyList<DiscoveredLobby>> WaitForLobbiesAsync(TimeSpan timeout, int minCount = 1)
    {
        var deadline = DateTime.UtcNow + timeout;
        var nextStatus = DateTime.UtcNow + TimeSpan.FromSeconds(2);
        while (DateTime.UtcNow < deadline)
        {
            var snap = Snapshot();
            if (snap.Count >= minCount) return snap;
            if (DateTime.UtcNow >= nextStatus)
            {
                Console.WriteLine(
                    $"[client-disc] waiting… probes={ProbesSent} rx={DatagramsReceived} " +
                    $"echo={EchoesIgnored} fail={ParseFailures} ok={RepliesReceived} lobbies={snap.Count}");
                nextStatus = DateTime.UtcNow + TimeSpan.FromSeconds(2);
            }
            await Task.Delay(200, _cts.Token);
        }
        return Snapshot();
    }

    private async Task ProbeLoopAsync(CancellationToken ct)
    {
        var magics = new[]
        {
            (LanDiscoveryHost.ProbeBonjourV2, LanDiscoveryHost.ProbeMagicBonjourV2),
            (LanDiscoveryHost.ProbeSosalbolt, LanDiscoveryHost.ProbeMagicSosalbolt),
        };
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var logRound = Interlocked.Increment(ref _probeRoundLogged) <= 2;
                foreach (var sock in _sockets)
                {
                    var local = (IPEndPoint)sock.LocalEndPoint!;
                    foreach (var destIp in BroadcastTargets(local.Address))
                    {
                        var dest = new IPEndPoint(destIp, LanDiscoveryHost.DiscoveryPort);
                        foreach (var (name, magic) in magics)
                        {
                            await sock.SendToAsync(magic, SocketFlags.None, dest, ct);
                            Interlocked.Increment(ref _probesSent);
                            if (logRound)
                            {
                                Console.WriteLine(
                                    $"[client-disc] TX probe '{name}' len={magic.Length} " +
                                    $"{local.Address}:{local.Port} → {dest}");
                            }
                        }
                    }
                }
                await Task.Delay(750, ct);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                Console.WriteLine($"[client-disc] probe error: {ex.Message}");
                await Task.Delay(500, ct);
            }
        }
    }

    private static IEnumerable<IPAddress> BroadcastTargets(IPAddress local)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var ep in LanInterfacePicker.GetLanIpv4Endpoints())
        {
            if (!ep.Address.Equals(local)) continue;
            if (seen.Add(ep.Broadcast.ToString()))
                yield return ep.Broadcast;
        }
        if (seen.Add(GlobalBroadcast.ToString()))
            yield return GlobalBroadcast;
    }

    private async Task ReceiveOneSocketAsync(Socket sock, CancellationToken ct)
    {
        var buf = new byte[2048];
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var result = await sock.ReceiveFromAsync(
                    buf.AsMemory(0, buf.Length),
                    SocketFlags.None,
                    new IPEndPoint(IPAddress.Any, 0),
                    ct);
                if (result.ReceivedBytes <= 0) continue;
                HandleReply(buf.AsSpan(0, result.ReceivedBytes), (IPEndPoint)result.RemoteEndPoint);
            }
            catch (OperationCanceledException) { break; }
            catch (ObjectDisposedException) { break; }
            catch (SocketException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (SocketException ex)
            {
                Console.WriteLine($"[client-disc] RX socket: {ex.SocketErrorCode} {ex.Message}");
                await Task.Delay(50, ct);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[client-disc] RX error: {ex.Message}");
                await Task.Delay(50, ct);
            }
        }
    }

    private void HandleReply(ReadOnlySpan<byte> buf, IPEndPoint from)
    {
        Interlocked.Increment(ref _datagrams);
        var hex = Convert.ToHexString(buf);

        // Own probes echoed back (broadcast loop) — still log once-ish via echo counter.
        if (buf.SequenceEqual(LanDiscoveryHost.ProbeMagicBonjourV2) ||
            buf.SequenceEqual(LanDiscoveryHost.ProbeMagicSosalbolt))
        {
            var n = Interlocked.Increment(ref _echoes);
            if (n <= 4)
                Console.WriteLine($"[client-disc] RX echo/probe from {from} len={buf.Length} hex={hex}");
            return;
        }

        // Always log raw RX so flaky parse / unexpected payloads are visible.
        Console.WriteLine($"[client-disc] RX from {from} len={buf.Length} hex={hex}");

        if (!LanDiscoveryInfo.TryDeserialize(buf, out var info))
        {
            Interlocked.Increment(ref _parseFails);
            Console.WriteLine($"[client-disc] RX parse FAIL (not LanDiscoveryInfo) from {from}");
            return;
        }

        Interlocked.Increment(ref _replies);
        var key = $"{from.Address}:{info.GamePort}:{info.HostName}:{info.LevelName}";
        lock (_gate)
        {
            if (_lobbies.TryGetValue(key, out var existing))
            {
                existing.Info = info;
                existing.Remote = from;
                existing.LastSeenUtc = DateTime.UtcNow;
                existing.HitCount++;
                Console.WriteLine(
                    $"[client-disc] RX again '{info.HostName}' / '{info.LevelName}' " +
                    $"hits={existing.HitCount} join={from.Address}:{info.GamePort}");
            }
            else
            {
                var lobby = new DiscoveredLobby
                {
                    Info = info,
                    Remote = from,
                    FirstSeenUtc = DateTime.UtcNow,
                    LastSeenUtc = DateTime.UtcNow,
                    HitCount = 1,
                };
                _lobbies[key] = lobby;
                Console.WriteLine(
                    $"[client-disc] FOUND '{info.HostName}' / '{info.LevelName}' " +
                    $"members={info.MemberCount} proto={info.ProtocolVersion} " +
                    $"join={from.Address}:{info.GamePort}");
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        var tasks = new List<Task>(_rxTasks.Count + 1);
        tasks.AddRange(_rxTasks);
        if (_probeTask is not null) tasks.Add(_probeTask);
        foreach (var t in tasks)
        {
            try { await t; } catch { /* ignore */ }
        }
        foreach (var s in _sockets)
        {
            try { s.Dispose(); } catch { /* ignore */ }
        }
        _sockets.Clear();
        _rxTasks.Clear();
        _cts.Dispose();
    }
}

public sealed class DiscoveredLobby
{
    public required LanDiscoveryInfo Info { get; set; }
    public required IPEndPoint Remote { get; set; }
    public DateTime FirstSeenUtc { get; set; }
    public DateTime LastSeenUtc { get; set; }
    public int HitCount { get; set; }

    public IPEndPoint JoinEndpoint => new(Remote.Address, Info.GamePort);
}

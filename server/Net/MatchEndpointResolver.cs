using System.Net;
using StandChillow.LanServer.Lan;
using StandChillow.LanServer.Net.Lobby;

namespace StandChillow.LanServer.Net;

/// <summary>
/// ConnectAsClient only: pick match LiteNetLib endpoint after op9.
/// Real phone client uses op9 IP as-is (<c>esz</c> → <c>LanLobbyHelper.qxm</c>) with no LAN remap.
/// We may prefer the lobby peer LAN IP when op9 is off our shared Wi‑Fi/Ethernet subnet
/// (PC VPN default route often cannot reach those advertised addresses).
/// </summary>
public static class MatchEndpointResolver
{
    public readonly record struct Resolution(
        IPEndPoint Endpoint,
        IPEndPoint? Fallback,
        IPAddress? BindAddress,
        string Reason);

    /// <param name="op9">Parsed OpGameHostingStateChangedEvent.</param>
    /// <param name="lobbyPeer">LiteNetLib lobby endpoint we already joined (LAN IP:7778).</param>
    /// <param name="forceMatchIp">Optional <c>--match-ip</c> override (keeps op9 port).</param>
    public static Resolution Resolve(
        GameHostingState op9,
        IPEndPoint lobbyPeer,
        IPAddress? forceMatchIp = null)
    {
        if (!op9.HasHosting || string.IsNullOrWhiteSpace(op9.Ip))
            throw new ArgumentException("op9 has no hosting endpoint", nameof(op9));

        var port = op9.Port == 0 ? GameMatchClient.DefaultMatchPort : op9.Port;
        var lobbyLan = lobbyPeer.Address;
        var bind = LanInterfacePicker.PickLanBindAddress(lobbyLan)
                   ?? LanInterfacePicker.PickLanBindAddress();

        if (forceMatchIp is not null)
        {
            var forced = new IPEndPoint(forceMatchIp, port);
            return new Resolution(
                forced,
                Fallback: SameEndpoint(forced, lobbyLan, port) ? null : new IPEndPoint(lobbyLan, port),
                bind,
                $"--match-ip override {forced}");
        }

        if (!IPAddress.TryParse(op9.Ip, out var op9Ip))
            throw new ArgumentException($"bad op9 IP '{op9.Ip}'", nameof(op9));

        var op9Ep = new IPEndPoint(op9Ip, port);
        var lobbyEp = new IPEndPoint(lobbyLan, port);

        // Same subnet as lobby peer (or our LAN NIC) → behave like real client: use op9 as-is.
        if (LanInterfacePicker.ShareLanSubnet(op9Ip, lobbyLan)
            || LanInterfacePicker.IsOnSharedLanSubnet(op9Ip))
        {
            return new Resolution(
                op9Ep,
                Fallback: SameEndpoint(op9Ep, lobbyLan, port) ? null : lobbyEp,
                bind,
                $"op9 on shared LAN subnet → {op9Ep}");
        }

        // Off-LAN advertised address (wrong NIC on host, CGNAT-ish iface, etc.).
        // Prefer lobby LAN immediately so PC VPN routing does not sink the connect.
        return new Resolution(
            lobbyEp,
            Fallback: null,
            bind,
            $"op9 advertised {op9Ip}; using lobby LAN {lobbyLan} for match " +
            $"(PC VPN may break routing to non-LAN)");
    }

    private static bool SameEndpoint(IPEndPoint ep, IPAddress ip, int port) =>
        ep.Port == port && ep.Address.Equals(ip);
}

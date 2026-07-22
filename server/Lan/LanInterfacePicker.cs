using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace StandChillow.LanServer.Lan;

/// <summary>
/// Picks real LAN (Wi‑Fi/Ethernet) addresses even when a VPN owns the default route.
/// </summary>
public static class LanInterfacePicker
{
    private static readonly string[] VpnNameHints =
    [
        "tun", "tap", "wg", "wireguard", "vpn", "ppp", "utun", "ipsec", "nordlynx", "proton",
        "hamachi", "zero-tier", "zerotier", "zt", "kakadu", "tailscale", "mullvad", "openvpn",
        "sing-box", "singbox", "clash", "v2ray", "outline", "warp", "cloudflare", "nebula"
    ];

    public static IReadOnlyList<LanEndpoint> GetLanIpv4Endpoints()
    {
        var result = new List<LanEndpoint>();

        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up)
                continue;
            if (nic.NetworkInterfaceType is NetworkInterfaceType.Loopback)
                continue;
            if (LooksLikeVpn(nic))
                continue;
            if (!IsLanCapableType(nic))
                continue;

            var props = nic.GetIPProperties();
            foreach (var uni in props.UnicastAddresses)
            {
                if (uni.Address.AddressFamily != AddressFamily.InterNetwork)
                    continue;
                if (IPAddress.IsLoopback(uni.Address))
                    continue;
                if (!IsPrivateOrLinkLocalIpv4(uni.Address))
                    continue;

                var mask = uni.IPv4Mask ?? GuessMask(uni.PrefixLength);
                var broadcast = ComputeBroadcast(uni.Address, mask);
                result.Add(new LanEndpoint(nic.Name, nic.Description, uni.Address, mask, broadcast));
            }
        }

        return result
            .GroupBy(e => e.Address.ToString())
            .Select(g => g.First())
            .OrderBy(e => e.Address.ToString(), StringComparer.Ordinal)
            .ToList();
    }

    public static string? FindInterfaceName(IPAddress address)
    {
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up) continue;
            foreach (var uni in nic.GetIPProperties().UnicastAddresses)
            {
                if (uni.Address.Equals(address))
                    return nic.Name;
            }
        }
        return null;
    }

    /// <summary>
    /// True if <paramref name="remote"/> sits on the same IPv4 subnet as any kept LAN NIC
    /// (Wi‑Fi/Ethernet), using that NIC's mask — not the VPN default route.
    /// </summary>
    public static bool IsOnSharedLanSubnet(IPAddress remote)
    {
        if (remote.AddressFamily != AddressFamily.InterNetwork)
            return false;
        foreach (var lan in GetLanIpv4Endpoints())
        {
            if (SameIpv4Subnet(remote, lan.Address, lan.SubnetMask))
                return true;
        }
        return false;
    }

    /// <summary>
    /// True if both addresses share the subnet of a kept LAN NIC that contains either address.
    /// Used to compare op9 advertised IP vs lobby peer (e.g. 192.168.1.73).
    /// </summary>
    public static bool ShareLanSubnet(IPAddress a, IPAddress b)
    {
        if (a.AddressFamily != AddressFamily.InterNetwork
            || b.AddressFamily != AddressFamily.InterNetwork)
            return false;

        foreach (var lan in GetLanIpv4Endpoints())
        {
            var aOn = SameIpv4Subnet(a, lan.Address, lan.SubnetMask);
            var bOn = SameIpv4Subnet(b, lan.Address, lan.SubnetMask);
            if (aOn && bOn)
                return true;
        }

        return false;
    }

    /// <summary>
    /// Prefer a LAN unicast address that shares a subnet with <paramref name="peer"/>,
    /// else the first kept LAN NIC. Used to bind LiteNetLib so PC VPN default route
    /// does not own the UDP sockets.
    /// Prefer 192.168/16 then 10/8 over other private ranges when advertising op9
    /// (avoids accidental non–Wi‑Fi 172.x ads when multiple NICs are kept).
    /// </summary>
    public static IPAddress? PickLanBindAddress(IPAddress? peer = null)
    {
        var lans = GetLanIpv4Endpoints();
        if (lans.Count == 0)
            return null;
        if (peer is not null)
        {
            foreach (var lan in lans)
            {
                if (SameIpv4Subnet(peer, lan.Address, lan.SubnetMask))
                    return lan.Address;
            }
        }

        static int Rank(IPAddress a)
        {
            var b = a.GetAddressBytes();
            if (b[0] == 192 && b[1] == 168) return 0;
            if (b[0] == 10) return 1;
            return 2;
        }

        return lans.OrderBy(e => Rank(e.Address)).ThenBy(e => e.Address.ToString(), StringComparer.Ordinal)
            .First().Address;
    }

    public static bool SameIpv4Subnet(IPAddress a, IPAddress b, IPAddress mask)
    {
        var aa = a.GetAddressBytes();
        var bb = b.GetAddressBytes();
        var mm = mask.GetAddressBytes();
        if (aa.Length != 4 || bb.Length != 4 || mm.Length != 4)
            return false;
        for (var i = 0; i < 4; i++)
        {
            if ((aa[i] & mm[i]) != (bb[i] & mm[i]))
                return false;
        }
        return true;
    }

    public static IReadOnlyList<string> DescribeSkippedInterfaces()
    {
        var kept = GetLanIpv4Endpoints().Select(e => e.InterfaceName).ToHashSet(StringComparer.Ordinal);
        var lines = new List<string>();
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up) continue;
            if (nic.NetworkInterfaceType is NetworkInterfaceType.Loopback) continue;
            if (kept.Contains(nic.Name)) continue;

            var addrs = nic.GetIPProperties().UnicastAddresses
                .Where(a => a.Address.AddressFamily == AddressFamily.InterNetwork)
                .Select(a => a.Address.ToString())
                .ToList();
            var why = LooksLikeVpn(nic) ? "vpn-hint"
                : !IsLanCapableType(nic) ? $"type={nic.NetworkInterfaceType}"
                : "no-private-ipv4";
            lines.Add($"{nic.Name} ({why}) addrs=[{string.Join(",", addrs)}]");
        }
        return lines;
    }

    private static bool IsLanCapableType(NetworkInterface nic)
    {
        if (nic.NetworkInterfaceType is
            NetworkInterfaceType.Ethernet or
            NetworkInterfaceType.Wireless80211 or
            NetworkInterfaceType.GigabitEthernet or
            NetworkInterfaceType.FastEthernetT or
            NetworkInterfaceType.FastEthernetFx)
            return true;

        // Some USB/Wi‑Fi stacks report Unknown; allow if not VPN-named.
        return nic.NetworkInterfaceType == NetworkInterfaceType.Unknown;
    }

    private static bool LooksLikeVpn(NetworkInterface nic)
    {
        var blob = $"{nic.Name} {nic.Description}".ToLowerInvariant();
        return VpnNameHints.Any(h => blob.Contains(h, StringComparison.Ordinal));
    }

    private static bool IsPrivateOrLinkLocalIpv4(IPAddress address)
    {
        var b = address.GetAddressBytes();
        return b[0] == 10
               || (b[0] == 172 && b[1] >= 16 && b[1] <= 31)
               || (b[0] == 192 && b[1] == 168)
               || (b[0] == 169 && b[1] == 254);
    }

    private static IPAddress GuessMask(int prefixLength)
    {
        if (prefixLength is < 0 or > 32)
            prefixLength = 24;
        uint mask = prefixLength == 0 ? 0u : uint.MaxValue << (32 - prefixLength);
        return new IPAddress(BitConverter.GetBytes(mask).Reverse().ToArray());
    }

    private static IPAddress ComputeBroadcast(IPAddress address, IPAddress mask)
    {
        var a = address.GetAddressBytes();
        var m = mask.GetAddressBytes();
        var b = new byte[4];
        for (var i = 0; i < 4; i++)
            b[i] = (byte)(a[i] | ~m[i]);
        return new IPAddress(b);
    }
}

public sealed record LanEndpoint(
    string InterfaceName,
    string Description,
    IPAddress Address,
    IPAddress SubnetMask,
    IPAddress Broadcast);

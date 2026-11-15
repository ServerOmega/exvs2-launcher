using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace Exvs2Launcher.Core.Net;

public sealed record NicAddress(
    string InterfaceName,
    string Description,
    string Address,
    NetworkInterfaceType Type,
    bool HasGateway,
    bool IsVirtual)
{
    public string Display => $"{Address}  ({InterfaceName}{(IsVirtual ? ", virtual" : "")})";
}

/// <summary>
/// Lists this PC's IPv4 addresses and picks the LAN address other machines on the home network use to reach it.
/// Virtual adapters (Radmin, TAP/n2n, ZeroTier, Tailscale, Hyper-V, VirtualBox, VMware, WSL) are listed but
/// never auto-picked over a real adapter.
/// </summary>
public static class LanAddressDetector
{
    private static readonly string[] VirtualHints =
    {
        "radmin", "tap-windows", "tap adapter", "openvpn", "n2n", "zerotier", "tailscale", "hamachi", "wireguard",
        "hyper-v", "vethernet", "virtualbox", "vmware", "wsl", "loopback", "npcap", "bluetooth", "teredo", "wintun",
    };

    public static bool LooksVirtual(string name, string description)
    {
        var s = (name + " " + description).ToLowerInvariant();
        return VirtualHints.Any(s.Contains);
    }

    public static IReadOnlyList<NicAddress> Enumerate()
    {
        var list = new List<NicAddress>();
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up) continue;
            if (nic.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel) continue;
            IPInterfaceProperties props;
            try { props = nic.GetIPProperties(); } catch (NetworkInformationException) { continue; }
            bool gw = props.GatewayAddresses.Any(g => g.Address.AddressFamily == AddressFamily.InterNetwork && !g.Address.Equals(IPAddress.Any));
            foreach (var ua in props.UnicastAddresses)
            {
                if (ua.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                if (IPAddress.IsLoopback(ua.Address)) continue;
                var ip = ua.Address.ToString();
                if (ip.StartsWith("169.254.", StringComparison.Ordinal)) continue;
                list.Add(new NicAddress(nic.Name, nic.Description, ip, nic.NetworkInterfaceType, gw, LooksVirtual(nic.Name, nic.Description)));
            }
        }
        return list;
    }

    public static NicAddress? PickBest(IEnumerable<NicAddress> candidates) =>
        candidates
            .OrderByDescending(Score)
            .ThenBy(c => c.InterfaceName, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();

    public static bool IsPrivate(string ip)
    {
        if (!IPAddress.TryParse(ip, out var a) || a.AddressFamily != AddressFamily.InterNetwork) return false;
        var b = a.GetAddressBytes();
        return b[0] == 10 || (b[0] == 172 && b[1] >= 16 && b[1] <= 31) || (b[0] == 192 && b[1] == 168);
    }

    private static int Score(NicAddress c)
    {
        int s = 0;
        if (!c.IsVirtual) s += 1000;
        if (c.HasGateway) s += 100;
        if (IsPrivate(c.Address)) s += 50;
        if (c.Address.StartsWith("192.168.", StringComparison.Ordinal)) s += 5;
        if (c.Type == NetworkInterfaceType.Ethernet) s += 10;
        else if (c.Type == NetworkInterfaceType.Wireless80211) s += 5;
        return s;
    }
}

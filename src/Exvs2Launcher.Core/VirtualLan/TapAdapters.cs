using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace Exvs2Launcher.Core.VirtualLan;

/// <summary>A network adapter, including ones that are down or have no address yet.</summary>
public sealed record AdapterInfo(string Name, string Description, bool IsUp, IReadOnlyList<string> IPv4);

public interface IAdapterSource
{
    IReadOnlyList<AdapterInfo> List();
}

public sealed class SystemAdapterSource : IAdapterSource
{
    public IReadOnlyList<AdapterInfo> List()
    {
        var list = new List<AdapterInfo>();
        NetworkInterface[] nics;
        try { nics = NetworkInterface.GetAllNetworkInterfaces(); }
        catch (NetworkInformationException) { return list; }
        foreach (var nic in nics)
        {
            var ips = new List<string>();
            try
            {
                foreach (var ua in nic.GetIPProperties().UnicastAddresses)
                    if (ua.Address.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(ua.Address))
                        ips.Add(ua.Address.ToString());
            }
            catch (NetworkInformationException) { }
            list.Add(new AdapterInfo(nic.Name, nic.Description, nic.OperationalStatus == OperationalStatus.Up, ips));
        }
        return list;
    }
}

public sealed record TapSelection(AdapterInfo? Adapter, string? Ip, string? Problem);

/// <summary>Finds the OpenVPN TAP-Windows adapter edge uses and its IPv4.</summary>
public static class TapAdapters
{
    /// <summary>Official OpenVPN download index for the signed tap-windows installer. The launcher never bundles it.</summary>
    public const string DownloadPage = "https://build.openvpn.net/downloads/releases/";
    public const string RecommendedInstaller = "tap-windows-9.24.7-I601-Win10.exe";
    public const string DriverHelpPage = "https://community.openvpn.net/Pages/Getting%20tap-windows";

    public static string MissingDriverMessage =>
        "No TAP-Windows adapter was found. n2n's edge.exe needs the OpenVPN TAP-Windows driver (tap0901 / tap-windows6). " +
        "Download the signed installer " + RecommendedInstaller + " from the official OpenVPN page " + DownloadPage +
        " and run it once as administrator, then try again. The launcher does not bundle or install the driver.";

    public static bool IsTap(AdapterInfo a)
    {
        var s = (a.Name + " " + a.Description).ToLowerInvariant();
        return s.Contains("tap-windows") || s.Contains("tap0901") || s.Contains("tap-win32") || s.Contains("tap-windows6");
    }

    public static IReadOnlyList<AdapterInfo> FindTaps(IEnumerable<AdapterInfo> all, string? adapterName = null)
    {
        var taps = all.Where(IsTap).ToList();
        if (string.IsNullOrWhiteSpace(adapterName)) return taps;
        var name = adapterName.Trim();
        // Renamed TAP adapters keep the driver description, so a named adapter must still look like TAP.
        // This keeps a typo such as "Ethernet" from binding the game to a real NIC in virtual LAN mode.
        return taps.Where(a => string.Equals(a.Name, name, StringComparison.OrdinalIgnoreCase)).ToList();
    }

    public static bool UsableIp(string? ip)
    {
        if (string.IsNullOrWhiteSpace(ip) || !IPAddress.TryParse(ip, out var a) || a.AddressFamily != AddressFamily.InterNetwork) return false;
        var b = a.GetAddressBytes();
        return !(b[0] is 0 or 127) && !(b[0] == 169 && b[1] == 254);
    }

    /// <summary>
    /// The TAP IPv4 to write into config.ini / lmnfig.ini. With a static address, only that exact address counts
    /// (Windows can keep an old address on a TAP briefly). Only TAP adapters are considered, never a real NIC.
    /// </summary>
    public static TapSelection Select(IEnumerable<AdapterInfo> all, VirtualLanSettings s)
    {
        var list = all.ToList();
        var taps = FindTaps(list, s.AdapterName);
        if (taps.Count == 0)
            return new TapSelection(null, null, string.IsNullOrWhiteSpace(s.AdapterName)
                ? MissingDriverMessage
                : (list.Any(IsTap)
                    ? "No TAP-Windows adapter named \"" + s.AdapterName.Trim() + "\" was found. Clear the TAP adapter name or use the name shown in Windows network connections."
                    : MissingDriverMessage));

        string? want = null;
        if (s.IpMode == VirtualIpMode.Static && VirtualLanConfig.TryParseStatic(s.StaticIp, out var st, out _))
            want = st!.Address.ToString();

        var candidates = new List<(AdapterInfo A, string Ip)>();
        foreach (var t in taps.Where(t => t.IsUp))
            foreach (var ip in t.IPv4.Where(UsableIp))
                if (want is null || ip == want)
                    candidates.Add((t, ip));

        if (candidates.Count == 1) return new TapSelection(candidates[0].A, candidates[0].Ip, null);
        if (candidates.Count > 1)
        {
            var names = string.Join(", ", candidates.Select(c => c.A.Name + " " + c.Ip));
            return new TapSelection(null, null, "More than one TAP adapter has an address (" + names + "). Set the TAP adapter name so the launcher knows which one edge uses.");
        }
        var first = taps.FirstOrDefault(t => t.IsUp) ?? taps[0];
        return new TapSelection(first, null, want is null
            ? "The TAP adapter has no usable IPv4 yet."
            : "The TAP adapter does not have " + want + " yet.");
    }
}

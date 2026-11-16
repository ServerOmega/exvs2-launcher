using System.Net;
using System.Net.Sockets;
using System.Text.Json.Serialization;

namespace Exvs2Launcher.Core.VirtualLan;

/// <summary>How edge gets its address on the virtual LAN.</summary>
public enum VirtualIpMode
{
    /// <summary>A fixed address such as 10.77.0.11/24 (edge <c>-a</c>).</summary>
    Static,
    /// <summary>The supernode hands out an address (edge without <c>-a</c>).</summary>
    SupernodeAssigned,
    /// <summary>A peer on the virtual LAN runs DHCP (edge <c>-r -a dhcp:0.0.0.0</c>).</summary>
    Dhcp,
}

/// <summary>
/// Optional n2n "Virtual LAN" mode (mode A in docs/n2n-research-2026-11-16.md): the whole cab lives on the TAP
/// adapter edge creates, so remote cabs see this LM as if it were on the same LAN. Off by default.
/// The encryption key is deliberately NOT a property here: profiles are saved as plain JSON that the user may
/// share. The key lives in <see cref="VirtualLanKeyStore"/> (DPAPI-protected on Windows).
/// </summary>
public sealed class VirtualLanSettings
{
    public const int DefaultMtu = 1290;
    public const int DefaultMetric = 1;
    public const int DefaultManagementPort = 5644;
    public const int DefaultSupernodePort = 7654;
    /// <summary>n2n 3.x community names are at most 19 characters (20 bytes with the terminator).</summary>
    public const int MaxCommunityLength = 19;

    public bool Enabled { get; set; }
    /// <summary>Supernode as host:port, for example sn.example.net:7654. No built-in public default.</summary>
    public string Supernode { get; set; } = "";
    /// <summary>
    /// Community (virtual LAN name). Treated as private: masked in the UI and logs, and kept in
    /// <see cref="VirtualLanKeyStore"/> with the key instead of the shareable profiles JSON.
    /// </summary>
    [JsonIgnore]
    public string Community { get; set; } = "";
    public VirtualIpMode IpMode { get; set; } = VirtualIpMode.Static;
    /// <summary>For <see cref="VirtualIpMode.Static"/>: an IPv4 with optional /prefix (default /24).</summary>
    public string StaticIp { get; set; } = "";
    public int Mtu { get; set; } = DefaultMtu;
    /// <summary>TAP interface metric (edge <c>-x</c>). Low so Windows sends LAN broadcasts out of the TAP.</summary>
    public int Metric { get; set; } = DefaultMetric;
    /// <summary>Accept multicast MACs (edge <c>-E</c>). On by default for this game.</summary>
    public bool Multicast { get; set; } = true;
    /// <summary>Always relay through the supernode (edge <c>-S1</c>), for CGNAT tests.</summary>
    public bool ForceRelay { get; set; }
    /// <summary>Header encryption (edge <c>-H</c>). Every edge and the supernode's community.list must agree.</summary>
    public bool HeaderEncryption { get; set; }
    /// <summary>Optional TAP adapter name (edge <c>-d</c>) when this PC has more than one TAP adapter.</summary>
    public string AdapterName { get; set; } = "";
    /// <summary>Optional edge description (edge <c>-I</c>), for example the cab or player name.</summary>
    public string Description { get; set; } = "";
    /// <summary>Path to a user-supplied edge.exe. The launcher never ships or downloads it.</summary>
    public string EdgePath { get; set; } = "";
    /// <summary>Edge management UDP port on localhost (edge <c>-t</c>).</summary>
    public int ManagementPort { get; set; } = DefaultManagementPort;
    public int StartTimeoutSeconds { get; set; } = 30;

    /// <summary>The TAP IPv4 the launcher last wrote into config.ini / lmnfig.ini, so internet mode can undo it.</summary>
    public string? LastVirtualIp { get; set; }

    public VirtualLanSettings Clone() => (VirtualLanSettings)MemberwiseClone();
}

public sealed record SupernodeAddress(string Host, int Port)
{
    public override string ToString() => Host.Contains(':') ? $"[{Host}]:{Port}" : $"{Host}:{Port}";
}

public sealed record StaticAddress(IPAddress Address, int PrefixLength)
{
    public override string ToString() => $"{Address}/{PrefixLength}";
}

/// <summary>Parsing and validation for <see cref="VirtualLanSettings"/>. Messages never contain the key.</summary>
public static class VirtualLanConfig
{
    public static bool TryParseSupernode(string? text, out SupernodeAddress? address, out string? error)
    {
        address = null;
        error = null;
        var t = (text ?? "").Trim();
        if (t.Length == 0) { error = "Enter the supernode as host:port (for example sn.example.net:7654)."; return false; }
        if (t.Any(char.IsWhiteSpace)) { error = "The supernode address cannot contain spaces."; return false; }
        if (t.Contains("://")) { error = "Enter the supernode as host:port, without http:// or udp://."; return false; }

        string host;
        string? portText;
        if (t.StartsWith('['))
        {
            int close = t.IndexOf(']');
            if (close < 0) { error = "Supernode: missing ] after an IPv6 address."; return false; }
            host = t[1..close];
            var rest = t[(close + 1)..];
            portText = rest.StartsWith(':') ? rest[1..] : (rest.Length == 0 ? null : "x");
        }
        else
        {
            int colon = t.LastIndexOf(':');
            if (colon >= 0 && t.IndexOf(':') != colon) { error = "Supernode: put an IPv6 address in brackets, for example [2001:db8::1]:7654."; return false; }
            host = colon >= 0 ? t[..colon] : t;
            portText = colon >= 0 ? t[(colon + 1)..] : null;
        }

        if (host.Length == 0) { error = "Supernode: the host is empty."; return false; }
        if (portText is null) { error = "Supernode: add the UDP port, for example " + host + ":" + VirtualLanSettings.DefaultSupernodePort + "."; return false; }
        if (!int.TryParse(portText, out int port) || port is < 1 or > 65535) { error = "Supernode: the port must be 1-65535."; return false; }
        if (!host.All(c => char.IsLetterOrDigit(c) || c is '.' or '-' or ':' or '_')) { error = "Supernode: the host has characters that are not allowed."; return false; }
        address = new SupernodeAddress(host, port);
        return true;
    }

    public static bool TryParseStatic(string? text, out StaticAddress? address, out string? error)
    {
        address = null;
        error = null;
        var t = (text ?? "").Trim();
        if (t.StartsWith("static:", StringComparison.OrdinalIgnoreCase)) t = t[7..];
        if (t.Length == 0) { error = "Enter a virtual IP such as 10.77.0.11/24, or choose supernode-assigned or DHCP."; return false; }
        int prefix = 24;
        var slash = t.IndexOf('/');
        if (slash >= 0)
        {
            if (!int.TryParse(t[(slash + 1)..], out prefix) || prefix is < 8 or > 30)
            { error = "Virtual IP: the /prefix must be 8-30 (usually /24)."; return false; }
            t = t[..slash];
        }
        if (!IPAddress.TryParse(t, out var ip) || ip.AddressFamily != AddressFamily.InterNetwork || t.Count(c => c == '.') != 3)
        { error = "Virtual IP \"" + t + "\" is not a valid IPv4 address."; return false; }
        var b = ip.GetAddressBytes();
        if (b[0] is 0 or 127 || b[0] >= 224 || (b[0] == 169 && b[1] == 254))
        { error = "Virtual IP " + ip + " cannot be used (loopback, link-local, multicast, or reserved)."; return false; }
        uint value = (uint)(b[0] << 24 | b[1] << 16 | b[2] << 8 | b[3]);
        uint host = value & (prefix == 32 ? 0u : (1u << (32 - prefix)) - 1);
        if (host == 0 || host == (1u << (32 - prefix)) - 1)
        { error = "Virtual IP " + ip + "/" + prefix + " is the network or broadcast address. Pick a host address."; return false; }
        address = new StaticAddress(ip, prefix);
        return true;
    }

    /// <summary>All problems that stop edge from starting. Empty when the settings are usable.</summary>
    public static IReadOnlyList<string> Validate(VirtualLanSettings s, string? key, Func<string, bool>? fileExists = null)
    {
        fileExists ??= File.Exists;
        var errors = new List<string>();
        if (!TryParseSupernode(s.Supernode, out _, out var snErr)) errors.Add(snErr!);

        var c = (s.Community ?? "").Trim();
        if (c.Length == 0) errors.Add("Enter the community (virtual LAN name) everyone in your group uses.");
        else if (c.Length > VirtualLanSettings.MaxCommunityLength) errors.Add($"The community name is too long (n2n allows {VirtualLanSettings.MaxCommunityLength} characters).");
        else if (c.Any(ch => char.IsWhiteSpace(ch) || char.IsControl(ch) || ch is '"' or '=')) errors.Add("The community name cannot contain spaces, quotes, or =.");

        if (string.IsNullOrEmpty(key))
            errors.Add("Enter the encryption key. Without -k, anyone in the community can read the game traffic.");
        else if (key.Any(ch => char.IsControl(ch)) || key != key.Trim())
            errors.Add("The encryption key cannot start or end with a space or contain line breaks.");

        if (s.IpMode == VirtualIpMode.Static && !TryParseStatic(s.StaticIp, out _, out var ipErr)) errors.Add(ipErr!);
        if (s.Mtu is < 576 or > 1500) errors.Add("MTU must be 576-1500 (default 1290).");
        if (s.Metric is < 0 or > 9999) errors.Add("Interface metric must be 0-9999 (default 1).");
        if (s.ManagementPort is < 1 or > 65535) errors.Add("The edge management port must be 1-65535 (default 5644).");
        if (s.StartTimeoutSeconds is < 5 or > 300) errors.Add("The virtual LAN wait time must be 5-300 seconds.");
        if (!string.IsNullOrWhiteSpace(s.AdapterName) && s.AdapterName.Any(ch => char.IsControl(ch) || ch == '"'))
            errors.Add("The TAP adapter name cannot contain quotes or line breaks.");
        if (!string.IsNullOrWhiteSpace(s.Description) && (s.Description.Length > 15 || s.Description.Any(ch => char.IsControl(ch) || ch == '"')))
            errors.Add("The edge description must be 15 characters or fewer, without quotes.");

        var edge = (s.EdgePath ?? "").Trim().Trim('"');
        if (edge.Length == 0) errors.Add("Pick your edge.exe (n2n). The launcher does not ship or download it.");
        else if (!edge.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) errors.Add("The edge path must point to an .exe file.");
        else if (!fileExists(edge)) errors.Add("edge.exe was not found at " + edge + ".");
        return errors;
    }
}

using System.Net;
using System.Net.Sockets;

namespace Exvs2Launcher.Core.Net;

/// <summary>
/// Addresses the LM hook and game can actually bind. 0.0.0.0, loopback, and 169.254.x are rejected,
/// and a chosen IP must be assigned to a local adapter or the LM fails with "failed to find interface".
/// </summary>
public static class BindAddress
{
    public const string UnspecifiedRejected =
        "0.0.0.0 is not a usable bind address. The LM hook needs an IPv4 that exists on a network adapter, or it fails with \"failed to find interface\". Pick a local address from the list.";

    public const string LinkLocalRejected =
        "169.254.x.x is a link-local (APIPA) address and is not a reliable bind address for the LM or the game. Pick a normal LAN address from the list.";

    public const string LoopbackRejected = LoopbackAddress.LmRejected;

    public static string NotAssigned(string ip, string role) =>
        ip + " is not assigned to a network adapter on this PC. The " + role +
        " needs an address that exists on an adapter, or it fails with \"failed to find interface\". Pick one of this PC's addresses.";

    public static bool IsUnspecified(string? ip)
    {
        if (string.IsNullOrWhiteSpace(ip)) return false;
        var t = ip.Trim();
        if (t is "0.0.0.0" or "*" or "::") return true;
        return IPAddress.TryParse(t, out var a) && (IPAddress.Any.Equals(a) || IPAddress.IPv6Any.Equals(a));
    }

    public static bool IsLinkLocal(string? ip)
    {
        if (string.IsNullOrWhiteSpace(ip) || !IPAddress.TryParse(ip.Trim(), out var a)) return false;
        if (a.AddressFamily != AddressFamily.InterNetwork) return false;
        var b = a.GetAddressBytes();
        return b[0] == 169 && b[1] == 254;
    }

    public static bool IsLoopback(string? ip) => LoopbackAddress.IsLoopback(ip);

    /// <summary>
    /// A reason the value cannot be used, or null if it is empty (not yet chosen) or is a real IPv4
    /// on a local adapter. When <paramref name="nics"/> is null, assignment is not checked.
    /// </summary>
    public static string? RejectReason(string? ip, string role, IReadOnlyList<NicAddress>? nics = null)
    {
        if (string.IsNullOrWhiteSpace(ip)) return null;
        var t = ip.Trim();
        if (IsUnspecified(t)) return role + " IP: " + UnspecifiedRejected;
        if (IsLoopback(t))
            return role + " IP: " + (string.Equals(role, "LM", StringComparison.OrdinalIgnoreCase)
                ? LoopbackRejected
                : "127.0.0.1 (loopback) is only reachable on this PC. Pick a LAN address from the list.");
        if (IsLinkLocal(t)) return role + " IP: " + LinkLocalRejected;
        if (!IPAddress.TryParse(t, out var a) || a.AddressFamily != AddressFamily.InterNetwork)
            return role + " IP \"" + t + "\" is not a valid IPv4 address.";
        if (nics is not null && NetworkBinding.FindLocal(t, nics) is null)
            return NotAssigned(t, role);
        return null;
    }

    public static bool IsUsableLocal(string? ip, IReadOnlyList<NicAddress> nics) =>
        !string.IsNullOrWhiteSpace(ip) && RejectReason(ip, "address", nics) is null;
}

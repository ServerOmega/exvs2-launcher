using System.Net;

namespace Exvs2Launcher.Core.Net;

/// <summary>
/// Loopback addresses (127.0.0.0/8, ::1, "localhost") are only reachable on this PC. An LM bound there
/// cannot be reached by a cab on a LAN address, which causes error 21-01.
/// </summary>
public static class LoopbackAddress
{
    public const string LmRejected =
        "127.0.0.1 (loopback) is only reachable on this PC. The game cannot reach an LM bound there from a LAN address, which causes error 21-01. Pick a LAN address instead.";

    public static bool IsLoopback(string? ip)
    {
        if (string.IsNullOrWhiteSpace(ip)) return false;
        var t = ip.Trim();
        if (t.Equals("localhost", StringComparison.OrdinalIgnoreCase)) return true;
        if (t.StartsWith("127.", StringComparison.Ordinal)) return true;
        return IPAddress.TryParse(t, out var a) && IPAddress.IsLoopback(a);
    }
}

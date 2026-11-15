using System.Net;
using System.Net.Sockets;

namespace Exvs2Launcher.Core.Net;

/// <summary>
/// The patched card server hosts the Blazor WebUI at the same listen address as the card API
/// (Kestrel <c>http://0.0.0.0:{port}</c>, fallback <c>index.html</c>, page routes <c>/</c> and <c>/Cards</c>).
/// </summary>
public static class WebUiUrl
{
    public const string DefaultPath = "/";
    public const string CardsPath = "/Cards";

    /// <summary>
    /// Builds <c>http://{host}:{port}/</c> from the profile card-server address. Bind-all addresses
    /// (<c>0.0.0.0</c>, <c>::</c>) are opened as localhost so the default browser can reach them.
    /// </summary>
    public static bool TryBuild(string? host, int port, out string? url, out string? error, string path = DefaultPath)
    {
        url = null;
        error = null;
        if (port is < 1 or > 65535)
        {
            error = "The card server port must be 1-65535.";
            return false;
        }

        var h = string.IsNullOrWhiteSpace(host) ? CardServerEndpoint.DefaultHost : host.Trim();
        if (IsUnspecified(h))
            h = LoopbackFor(h);

        if (h.Length == 0)
        {
            error = "The card server address is missing.";
            return false;
        }

        var route = string.IsNullOrWhiteSpace(path) ? DefaultPath : path.Trim();
        if (!route.StartsWith('/')) route = "/" + route;

        string authority;
        if (IPAddress.TryParse(h, out var ip) && ip.AddressFamily == AddressFamily.InterNetworkV6)
            authority = "[" + ip + "]:" + port;
        else
            authority = h + ":" + port;

        url = "http://" + authority + route;
        return true;
    }

    public static string? BuildOrNull(string? host, int port, string path = DefaultPath) =>
        TryBuild(host, port, out var url, out _, path) ? url : null;

    private static bool IsUnspecified(string host) =>
        host is "0.0.0.0" or "*" or "+" ||
        (IPAddress.TryParse(host, out var ip) && IPAddress.Any.Equals(ip)) ||
        (IPAddress.TryParse(host, out ip) && IPAddress.IPv6Any.Equals(ip));

    private static string LoopbackFor(string host) =>
        IPAddress.TryParse(host, out var ip) && ip.AddressFamily == AddressFamily.InterNetworkV6
            ? IPAddress.IPv6Loopback.ToString()
            : CardServerEndpoint.DefaultHost;
}

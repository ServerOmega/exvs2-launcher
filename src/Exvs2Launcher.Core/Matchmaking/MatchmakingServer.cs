using System.Net;
using System.Text.Json.Serialization;
using Exvs2Launcher.Core.Wizard;

namespace Exvs2Launcher.Core.Matchmaking;

/// <summary>
/// One saved matchmaking server (shown in the launcher's dropdown). Only a name, host and port: no secrets.
/// The card server talks to it at <c>http://host:port/match</c>; the launcher reads <c>http://host:port/status</c>.
/// </summary>
public sealed class MatchmakingServer
{
    public string Name { get; set; } = "";
    public string Host { get; set; } = "";
    public int Port { get; set; } = CardServerConfig.DefaultMatchmakerPort;

    [JsonIgnore] public bool HasAddress => !string.IsNullOrWhiteSpace(Host);
    [JsonIgnore] public string Address => HasAddress ? $"{Host.Trim()}:{Port}" : "(no address yet)";

    /// <summary>The value for the card server's <c>CardServerConfig.OnlineMatching.MatchUrl</c>.</summary>
    [JsonIgnore] public string? MatchUrl => HasAddress ? $"http://{UriHost(Host.Trim())}:{Port}/match" : null;

    [JsonIgnore] public Uri? StatusUri =>
        HasAddress && Uri.TryCreate($"http://{UriHost(Host.Trim())}:{Port}/status", UriKind.Absolute, out var u) ? u : null;

    public MatchmakingServer Clone() => new() { Name = Name, Host = Host, Port = Port };

    public override string ToString() => Name;

    internal static string UriHost(string host) =>
        IPAddress.TryParse(host, out var ip) && ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6
            ? "[" + host.Trim('[', ']') + "]"
            : host;
}

/// <summary>Defaults, validation and lookups for the saved matchmaking server list.</summary>
public static class MatchmakingServers
{
    /// <summary>
    /// The community servers. Their addresses are not stored in this public repository: the host is left blank for
    /// the user to fill in (ask in the Discord), so the defaults never point anywhere by themselves.
    /// </summary>
    public static List<MatchmakingServer> Defaults() => new()
    {
        new MatchmakingServer { Name = "LA (Los Angeles)", Host = "", Port = CardServerConfig.DefaultMatchmakerPort },
        new MatchmakingServer { Name = "EU (Europe)", Host = "", Port = CardServerConfig.DefaultMatchmakerPort },
    };

    /// <summary>
    /// Validates a server about to be added or edited. Accepts a host, host:port or a pasted match URL in
    /// <paramref name="hostInput"/> (the port in it wins over <paramref name="port"/>). Blank host is allowed
    /// (a placeholder to fill in later). Returns null and the cleaned server, or an error.
    /// </summary>
    public static string? TryCreate(string? name, string? hostInput, string? portText, IEnumerable<MatchmakingServer> others,
        out MatchmakingServer server)
    {
        server = new MatchmakingServer();
        var n = (name ?? "").Trim();
        if (n.Length == 0) return "Give the server a name, for example LA or EU.";
        if (n.Length > 60) return "Keep the name under 60 characters.";
        if (others.Any(o => string.Equals(o.Name.Trim(), n, StringComparison.OrdinalIgnoreCase)))
            return "There is already a server called \"" + n + "\".";

        int port = CardServerConfig.DefaultMatchmakerPort;
        if (!string.IsNullOrWhiteSpace(portText) && (!int.TryParse(portText.Trim(), out port) || port is < 1 or > 65535))
            return "The port must be a number from 1 to 65535 (the public matchmaker uses 28090).";

        var host = (hostInput ?? "").Trim();
        if (host.Length > 0)
        {
            var raw = host.Contains("://", StringComparison.Ordinal) ? host : "http://" + host;
            if (!Uri.TryCreate(raw, UriKind.Absolute, out var uri) || string.IsNullOrEmpty(uri.Host) ||
                (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
                return "That does not look like a host name or IP address.";
            if (uri.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase) ||
                (IPAddress.TryParse(uri.Host.Trim('[', ']'), out var ip) && IPAddress.IsLoopback(ip)))
                return "Use the matchmaker's public host or LAN IP, not 127.0.0.1 / localhost.";
            host = uri.Host.Trim('[', ']');
            if (!uri.IsDefaultPort) port = uri.Port;
        }
        server = new MatchmakingServer { Name = n, Host = host, Port = port };
        return null;
    }

    /// <summary>The saved server whose host and port match a card server's MatchUrl, or null.</summary>
    public static MatchmakingServer? FindByMatchUrl(IEnumerable<MatchmakingServer> servers, string? matchUrl)
    {
        if (!CardServerConfig.TryNormalizeMatchUrl(matchUrl, out var url, out var host, out _) || url is null || host is null)
            return null;
        var port = new Uri(url).Port;
        return servers.FirstOrDefault(s => s.HasAddress && s.Port == port &&
                                           string.Equals(s.Host.Trim().Trim('[', ']'), host.Trim('[', ']'), StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Puts the defaults in an empty / missing list (first run). Existing lists are left alone.</summary>
    public static List<MatchmakingServer> EnsureDefaults(List<MatchmakingServer>? list) =>
        list is { Count: > 0 } ? list : Defaults();
}

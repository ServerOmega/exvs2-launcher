using System.Net;

namespace Exvs2Launcher.Core.Net;

/// <summary>
/// The LM and game talk to the card server at <c>Server=host:port</c> in <c>config.ini</c> and
/// <c>lmnfig.ini</c>. A value with no port, or the LM's own IP, causes error 21-32.
/// </summary>
public static class CardServerEndpoint
{
    public const string DefaultHost = "127.0.0.1";
    public const int DefaultPort = 28080;
    public const string Default = "127.0.0.1:28080";

    public static string Expected(string? host, int port)
    {
        var h = string.IsNullOrWhiteSpace(host) ? DefaultHost : host.Trim();
        var p = port is < 1 or > 65535 ? DefaultPort : port;
        return Format(h, p);
    }

    public static string Format(string host, int port) => host.Trim() + ":" + port;

    public static bool TryParse(string? value, out string host, out int port)
    {
        host = "";
        port = 0;
        if (string.IsNullOrWhiteSpace(value)) return false;
        var t = value.Trim();
        int colon;
        if (t.StartsWith('['))
        {
            int close = t.IndexOf(']');
            if (close < 2 || close + 2 >= t.Length || t[close + 1] != ':') return false;
            host = t[1..close];
            return int.TryParse(t[(close + 2)..], out port) && port is >= 1 and <= 65535 && host.Length > 0;
        }

        colon = t.LastIndexOf(':');
        if (colon <= 0 || colon == t.Length - 1) return false;
        host = t[..colon].Trim();
        return host.Length > 0 && !host.Contains(':') &&
               int.TryParse(t[(colon + 1)..], out port) && port is >= 1 and <= 65535;
    }

    public static bool Same(string? a, string? b)
    {
        if (!TryParse(a, out var ha, out var pa) || !TryParse(b, out var hb, out var pb))
            return false;
        if (pa != pb) return false;
        if (ha.Equals(hb, StringComparison.OrdinalIgnoreCase)) return true;
        if (LoopbackAddress.IsLoopback(ha) && LoopbackAddress.IsLoopback(hb)) return true;
        return IPAddress.TryParse(ha, out var ia) && IPAddress.TryParse(hb, out var ib) && ia.Equals(ib);
    }

    /// <summary>Why <paramref name="iniValue"/> is not a usable <c>Server=</c> for the card server at <paramref name="expected"/>, or null if it is fine.</summary>
    public static string? Problem(string? iniValue, string fileLabel, string expected)
    {
        if (string.IsNullOrWhiteSpace(iniValue))
            return fileLabel + " has no Server= value. The LM talks to the card server at this address. Set it to " +
                   expected + " or the LM fails with error 21-32.";

        var raw = iniValue.Trim();
        if (!TryParse(raw, out _, out _))
            return fileLabel + " Server=" + raw + " is not a host:port address (a port is required). It must match the card server, usually " +
                   expected + ", or the LM fails with error 21-32.";

        if (!Same(raw, expected))
            return fileLabel + " Server=" + raw + " does not match the profile card server " + expected +
                   ". The LM will fail with error 21-32 if it cannot reach the card server there.";
        return null;
    }

    public static IReadOnlyList<string> Inspect(string? clientServer, string? lmServer, string expected)
    {
        var list = new List<string>();
        var cfg = Problem(clientServer, "config.ini", expected);
        var lm = Problem(lmServer, "lmnfig.ini", expected);
        if (cfg is not null) list.Add(cfg);
        if (lm is not null) list.Add(lm);
        return list;
    }

    public static string Unreachable(string expected) =>
        "The card server at " + expected + " is not answering. Start it first, or the LM fails with error 21-32.";

    public static string OfferFix(string expected) =>
        "Write Server=" + expected + " to config.ini and lmnfig.ini? A backup is made if a file changes. " +
        "Error 21-32 means the LM cannot reach the card server.";
}

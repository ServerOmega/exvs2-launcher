using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace Exvs2Launcher.Core.VirtualLan;

/// <summary>Peers edge knows about. Relayed = still going through the supernode (no direct UDP path yet).</summary>
public sealed record EdgePeers(int Direct, int Relayed, bool Parsed)
{
    public int Total => Direct + Relayed;
    public static readonly EdgePeers Unknown = new(0, 0, false);
    public string Describe() => !Parsed ? "peers unknown" : Total == 0 ? "no peers yet" : $"{Total} peer(s): {Direct} direct, {Relayed} via supernode";
}

/// <summary>
/// Parses edge's management reply. n2n 3.1+ answers <c>r 1 edges</c> with JSON rows (mode p2p / pSp);
/// n2n 3.0 does not know that command and replies with its text status table instead, which has
/// a "SUPERNODE FORWARD" (relayed) and a "PEER TO PEER" (direct) section.
/// </summary>
public static class EdgeStatusParser
{
    public static EdgePeers Parse(IEnumerable<string> packets)
    {
        var text = string.Join("\n", packets);
        if (string.IsNullOrWhiteSpace(text)) return EdgePeers.Unknown;
        var lines = text.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).ToList();
        if (lines.Any(l => l.StartsWith('{'))) return ParseJson(lines);
        return ParseLegacy(lines);
    }

    private static EdgePeers ParseJson(List<string> lines)
    {
        int direct = 0, relayed = 0;
        bool sawEnd = false, sawBegin = false;
        foreach (var l in lines.Where(l => l.StartsWith('{')))
        {
            try
            {
                using var doc = JsonDocument.Parse(l);
                var root = doc.RootElement;
                if (!root.TryGetProperty("_type", out var type)) continue;
                var t = type.GetString();
                if (t == "begin") sawBegin = true;
                else if (t == "end") sawEnd = true;
                else if (t == "error") return EdgePeers.Unknown;
                else if (t == "row")
                {
                    var mode = root.TryGetProperty("mode", out var m) ? m.GetString() : null;
                    if (string.Equals(mode, "p2p", StringComparison.OrdinalIgnoreCase)) direct++;
                    else if (string.Equals(mode, "pSp", StringComparison.OrdinalIgnoreCase)) relayed++;
                }
            }
            catch (JsonException) { }
        }
        return sawBegin || sawEnd || direct + relayed > 0 ? new EdgePeers(direct, relayed, true) : EdgePeers.Unknown;
    }

    private static EdgePeers ParseLegacy(List<string> lines)
    {
        int direct = 0, relayed = 0;
        string? section = null;
        bool sawSection = false;
        foreach (var l in lines)
        {
            var upper = l.ToUpperInvariant();
            if (upper.Contains("SUPERNODE FORWARD") || upper.StartsWith("PENDING")) { section = "relay"; sawSection = true; continue; }
            if (upper.Contains("PEER TO PEER") || upper.StartsWith("KNOWN")) { section = "p2p"; sawSection = true; continue; }
            if (upper.StartsWith("SUPERNODE") || upper.StartsWith("UPTIME") || upper.StartsWith("COMMUNITY") || upper.StartsWith("TOTAL") || upper.StartsWith("LAST"))
            { section = null; continue; }
            if (l.StartsWith('=') || l.StartsWith('-') || l.StartsWith("###")) continue;
            if (section is null) continue;
            // A peer row has a MAC address (aa:bb:cc:dd:ee:ff).
            if (!HasMac(l)) continue;
            if (section == "p2p") direct++; else relayed++;
        }
        return sawSection ? new EdgePeers(direct, relayed, true) : EdgePeers.Unknown;
    }

    private static bool HasMac(string l)
    {
        foreach (var token in l.Split(new[] { ' ', '|', '\t' }, StringSplitOptions.RemoveEmptyEntries))
            if (token.Length == 17 && token.Count(c => c == ':') == 5 && token.Replace(":", "").All(Uri.IsHexDigit))
                return true;
        return false;
    }
}

/// <summary>Talks to edge's management port on localhost (UDP). Never sends or logs the key.</summary>
public interface IEdgeManagement
{
    Task<EdgePeers> QueryPeersAsync(int port, CancellationToken ct);
    /// <summary>Asks edge to exit cleanly so the supernode releases this MAC/IP at once.</summary>
    Task RequestStopAsync(int port, CancellationToken ct);
}

public sealed class UdpEdgeManagement : IEdgeManagement
{
    /// <summary>n2n 3.1+ default management password; 3.0 ignores it and accepts a plain "stop".</summary>
    public const string DefaultManagementPassword = "n2n";

    public async Task<EdgePeers> QueryPeersAsync(int port, CancellationToken ct)
    {
        using var udp = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var target = new IPEndPoint(IPAddress.Loopback, port);
        var req = Encoding.ASCII.GetBytes("r 1 edges\n");
        await udp.SendAsync(req, target, ct).ConfigureAwait(false);
        var packets = new List<string>();
        var deadline = DateTime.UtcNow + TimeSpan.FromMilliseconds(800);
        while (DateTime.UtcNow < deadline)
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(deadline - DateTime.UtcNow);
            try
            {
                var r = await udp.ReceiveAsync(cts.Token).ConfigureAwait(false);
                var s = Encoding.UTF8.GetString(r.Buffer);
                packets.Add(s);
                if (s.Contains("\"_type\":\"end\"")) break;
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested) { break; }
            catch (SocketException) { break; }
        }
        return EdgeStatusParser.Parse(packets);
    }

    public async Task RequestStopAsync(int port, CancellationToken ct)
    {
        using var udp = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var target = new IPEndPoint(IPAddress.Loopback, port);
        foreach (var cmd in new[] { "w 1:1:" + DefaultManagementPassword + " stop\n", "stop\n" })
        {
            try { await udp.SendAsync(Encoding.ASCII.GetBytes(cmd), target, ct).ConfigureAwait(false); }
            catch (SocketException) { }
        }
    }
}

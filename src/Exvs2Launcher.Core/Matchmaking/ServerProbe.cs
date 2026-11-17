using System.Diagnostics;
using System.Net.Sockets;

namespace Exvs2Launcher.Core.Matchmaking;

public enum ProbeMethod { None, Http, Tcp }

/// <summary>Result of pinging one matchmaking server.</summary>
public sealed record ProbeResult(long? PingMs, ProbeMethod Method, MatchmakerStatusInfo? Status, string? Note)
{
    public bool Reachable => PingMs is not null;

    public static ProbeResult NoAddress { get; } = new(null, ProbeMethod.None, null, "no address yet");

    /// <summary>Short text for the dropdown, e.g. "42 ms · 6 online (2 searching, 4 in match)".</summary>
    public string Summary
    {
        get
        {
            if (Method == ProbeMethod.None && Note == NoAddress.Note) return "no address yet";
            if (!Reachable) return "offline" + (Note is null ? "" : " (" + Note + ")");
            var ping = $"{PingMs} ms" + (Method == ProbeMethod.Tcp ? " (TCP)" : "");
            return Status is not null ? ping + " · " + Status.Players : ping + " · player count not available";
        }
    }
}

/// <summary>
/// Pings a matchmaker with one small HTTP GET to its public <c>/status</c> (which also returns the player totals).
/// When HTTP gets no answer at all, falls back to timing a TCP connect to the same port. Any HTTP reply (403 / 404
/// from an older server) still counts as a ping; only a valid /status body gives player counts.
/// </summary>
public sealed class ServerProbe
{
    private readonly HttpClient _http;
    private readonly TimeSpan _timeout;

    public ServerProbe(HttpClient? http = null, TimeSpan? timeout = null)
    {
        _http = http ?? CreateClient();
        _timeout = timeout ?? TimeSpan.FromSeconds(4);
    }

    public static HttpClient CreateClient()
    {
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            PooledConnectionIdleTimeout = TimeSpan.FromSeconds(30),
            ConnectTimeout = TimeSpan.FromSeconds(4),
            UseProxy = false,
        };
        var c = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        c.DefaultRequestHeaders.UserAgent.ParseAdd("exvs2-launcher");
        return c;
    }

    public async Task<ProbeResult> ProbeAsync(MatchmakingServer server, CancellationToken ct = default)
    {
        var uri = server.StatusUri;
        if (uri is null) return ProbeResult.NoAddress;

        string? httpError;
        using (var cts = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            cts.CancelAfter(_timeout);
            var sw = Stopwatch.StartNew();
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Get, uri);
                using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cts.Token).ConfigureAwait(false);
                var ms = sw.ElapsedMilliseconds;
                MatchmakerStatusInfo? status = null;
                string? note = null;
                if (resp.IsSuccessStatusCode)
                {
                    var body = await ReadLimitedAsync(resp, 64 * 1024, cts.Token).ConfigureAwait(false);
                    status = MatchmakerStatusInfo.TryParse(body);
                    if (status is null) note = "no status reply";
                }
                else note = "status page not available (HTTP " + (int)resp.StatusCode + ")";
                return new ProbeResult(Math.Max(1, ms), ProbeMethod.Http, status, note);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested) { httpError = "timed out"; }
            catch (HttpRequestException ex) { httpError = ex.InnerException?.Message ?? ex.Message; }
        }

        // HTTP did not answer: time a plain TCP connect to the same port
        using (var cts = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            cts.CancelAfter(_timeout);
            var sw = Stopwatch.StartNew();
            try
            {
                using var tcp = new TcpClient();
                await tcp.ConnectAsync(server.Host.Trim().Trim('[', ']'), server.Port, cts.Token).ConfigureAwait(false);
                return new ProbeResult(Math.Max(1, sw.ElapsedMilliseconds), ProbeMethod.Tcp, null, "HTTP: " + httpError);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return new ProbeResult(null, ProbeMethod.None, null, "timed out"); }
            catch (SocketException ex) { return new ProbeResult(null, ProbeMethod.None, null, ex.SocketErrorCode.ToString()); }
        }
    }

    private static async Task<string> ReadLimitedAsync(HttpResponseMessage resp, int max, CancellationToken ct)
    {
        await using var s = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        var buf = new byte[max + 1];
        int total = 0, n;
        while (total <= max && (n = await s.ReadAsync(buf.AsMemory(total, buf.Length - total), ct).ConfigureAwait(false)) > 0)
            total += n;
        return total > max ? "" : System.Text.Encoding.UTF8.GetString(buf, 0, total);
    }
}

using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Exvs2Launcher.Core.Files;
using Exvs2Launcher.Core.Matchmaking;
using Exvs2Launcher.Core.Profiles;
using Exvs2Launcher.Core.Wizard;

namespace Exvs2Launcher.Core.Tests;

public class MatchmakingServerTests
{
    [Fact]
    public void Defaults_are_LA_and_EU_with_blank_hosts_on_28090()
    {
        var d = MatchmakingServers.Defaults();
        Assert.Equal(2, d.Count);
        Assert.Contains("LA", d[0].Name);
        Assert.Contains("EU", d[1].Name);
        Assert.All(d, s => { Assert.Equal("", s.Host); Assert.Equal(28090, s.Port); Assert.Null(s.MatchUrl); Assert.Null(s.StatusUri); });
    }

    [Theory]
    [InlineData("mm.example.org", "", "mm.example.org", 28090)]
    [InlineData("mm.example.org:28100", "", "mm.example.org", 28100)]
    [InlineData("http://203.0.113.10:28090/match", "", "203.0.113.10", 28090)]
    [InlineData("203.0.113.10", "28095", "203.0.113.10", 28095)]
    [InlineData("", "", "", 28090)]
    public void TryCreate_accepts_host_host_port_and_pasted_match_urls(string input, string port, string host, int expectPort)
    {
        var err = MatchmakingServers.TryCreate("Mine", input, port, Array.Empty<MatchmakingServer>(), out var s);
        Assert.Null(err);
        Assert.Equal(host, s.Host);
        Assert.Equal(expectPort, s.Port);
        if (host.Length > 0)
        {
            Assert.Equal($"http://{host}:{expectPort}/match", s.MatchUrl);
            Assert.Equal($"http://{host}:{expectPort}/status", s.StatusUri!.ToString());
        }
    }

    [Theory]
    [InlineData("", "mm.example.org", "", "name")]
    [InlineData("LA (Los Angeles)", "mm.example.org", "", "already")]
    [InlineData("X", "127.0.0.1", "", "127.0.0.1")]
    [InlineData("X", "localhost", "", "localhost")]
    [InlineData("X", "mm.example.org", "70000", "port")]
    [InlineData("X", "ftp://mm.example.org", "", "host")]
    public void TryCreate_rejects_bad_input(string name, string host, string port, string expect)
    {
        var err = MatchmakingServers.TryCreate(name, host, port, MatchmakingServers.Defaults(), out _);
        Assert.NotNull(err);
        Assert.Contains(expect, err!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void FindByMatchUrl_matches_host_and_port()
    {
        var list = new List<MatchmakingServer>
        {
            new() { Name = "A", Host = "a.example.org", Port = 28090 },
            new() { Name = "B", Host = "203.0.113.7", Port = 28090 },
            new() { Name = "Blank", Host = "", Port = 28090 },
        };
        Assert.Equal("B", MatchmakingServers.FindByMatchUrl(list, "http://203.0.113.7:28090/match")?.Name);
        Assert.Equal("A", MatchmakingServers.FindByMatchUrl(list, "A.EXAMPLE.ORG:28090")?.Name);
        Assert.Null(MatchmakingServers.FindByMatchUrl(list, "http://203.0.113.7:28080/match"));
        Assert.Null(MatchmakingServers.FindByMatchUrl(list, ""));
    }

    [Fact]
    public void Server_list_is_saved_with_the_profiles_and_defaults_fill_an_empty_list()
    {
        using var dir = new TempDir();
        var store = new ProfileStore(Path.Combine(dir.Root, ProfileStore.FileName));
        var file = new ProfileFile { MatchmakingServers = MatchmakingServers.EnsureDefaults(null) };
        file.MatchmakingServers.Add(new MatchmakingServer { Name = "Home", Host = "192.168.1.20", Port = 28090 });
        file.SelectedMatchmakingServer = "Home";
        store.Save(file);
        var text = File.ReadAllText(store.FilePath);
        Assert.DoesNotContain("MatchUrl", text);   // only name/host/port are stored
        var back = store.Load();
        Assert.Equal(3, back.MatchmakingServers!.Count);
        Assert.Equal("192.168.1.20", back.MatchmakingServers[2].Host);
        Assert.Equal("Home", back.SelectedMatchmakingServer);
        Assert.Same(back.MatchmakingServers, MatchmakingServers.EnsureDefaults(back.MatchmakingServers));
        Assert.Equal(2, MatchmakingServers.EnsureDefaults(new()).Count);
    }

    [Fact]
    public void Selecting_a_server_writes_MatchUrl_and_StunHost_inside_CardServerConfig_with_a_backup()
    {
        using var dir = new TempDir();
        var json = Path.Combine(dir.Root, "Configurations", "server.json");
        Directory.CreateDirectory(Path.GetDirectoryName(json)!);
        File.WriteAllText(json, """
            {
              "CardServerConfig": {
                "OnlineMatching": { "Enabled": true, "MatchUrl": "http://old.example.org:28090/match", "StunHost": "old.example.org", "StunPort": 3478 },
                "OnlineTag": { "TagSecret": "keep-this-secret-1" }
              }
            }
            """);
        var server = new MatchmakingServer { Name = "EU", Host = "eu.example.org", Port = 28090 };
        var r = CardServerConfig.Apply(json, server.MatchUrl, null, new BackupService(() => new DateTime(2026, 11, 16, 16, 0, 0)));
        Assert.Contains(json, r.Written);
        Assert.Single(r.Backups);
        Assert.Contains("old.example.org", File.ReadAllText(r.Backups[0]));
        using var doc = JsonDocument.Parse(File.ReadAllText(json));
        var om = doc.RootElement.GetProperty("CardServerConfig").GetProperty("OnlineMatching");
        Assert.Equal("http://eu.example.org:28090/match", om.GetProperty("MatchUrl").GetString());
        Assert.Equal("eu.example.org", om.GetProperty("StunHost").GetString());
        Assert.Equal(3478, om.GetProperty("StunPort").GetInt32());
        Assert.False(doc.RootElement.TryGetProperty("OnlineMatching", out _));   // not written at the top level
        Assert.Equal("keep-this-secret-1", doc.RootElement.GetProperty("CardServerConfig").GetProperty("OnlineTag").GetProperty("TagSecret").GetString());
        var pub = CardServerConfig.ReadPublic(json)!;
        Assert.Equal("http://eu.example.org:28090/match", pub.MatchUrl);
        Assert.True(pub.HasTagSecret);
        Assert.Equal("EU", MatchmakingServers.FindByMatchUrl(new[] { server }, pub.MatchUrl)?.Name);
    }
}

public class MatchmakerStatusTests
{
    [Fact]
    public void Parses_the_status_reply()
    {
        var s = MatchmakerStatusInfo.TryParse("""{"service":"exvs2-matchmaking","version":"0.6.0-dev","online":16,"searching":14,"inMatch":2,"matches":1,"updatedAt":"2026-11-17T00:06:39Z","cacheSeconds":5}""");
        Assert.NotNull(s);
        Assert.Equal(16, s!.Online);
        Assert.Equal(14, s.Searching);
        Assert.Equal(2, s.InMatch);
        Assert.Equal(1, s.Matches);
        Assert.Equal("0.6.0-dev", s.Version);
        Assert.Equal("16 online (14 searching, 2 in match)", s.Players);
    }

    [Theory]
    [InlineData("")]
    [InlineData("<html>forbidden</html>")]
    [InlineData("[]")]
    [InlineData("""{"online":"x","searching":1,"inMatch":0}""")]
    [InlineData("""{"searching":1,"inMatch":0}""")]
    public void Rejects_other_bodies(string body) => Assert.Null(MatchmakerStatusInfo.TryParse(body));
}

public class ServerProbeTests
{
    /// <summary>Tiny one-shot-per-connection HTTP server on loopback.</summary>
    private sealed class MiniServer : IDisposable
    {
        private readonly TcpListener _l = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _cts = new();
        public int Port => ((IPEndPoint)_l.LocalEndpoint).Port;
        public string? LastPath;

        public MiniServer(Func<string, (int Code, string Body)?> reply)
        {
            _l.Start();
            _ = Task.Run(async () =>
            {
                while (!_cts.IsCancellationRequested)
                {
                    TcpClient c;
                    try { c = await _l.AcceptTcpClientAsync(_cts.Token); } catch { return; }
                    _ = Task.Run(async () =>
                    {
                        using (c)
                        {
                            var s = c.GetStream();
                            var buf = new byte[4096];
                            int n = await s.ReadAsync(buf);
                            var line = Encoding.ASCII.GetString(buf, 0, n).Split("\r\n")[0];
                            LastPath = line.Split(' ').ElementAtOrDefault(1);
                            var r = reply(LastPath ?? "");
                            if (r is null) { await Task.Delay(5000); return; }   // accept, never answer
                            var body = Encoding.UTF8.GetBytes(r.Value.Body);
                            var head = $"HTTP/1.1 {r.Value.Code} X\r\nContent-Type: application/json\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n";
                            await s.WriteAsync(Encoding.ASCII.GetBytes(head));
                            await s.WriteAsync(body);
                        }
                    });
                }
            });
        }

        public void Dispose() { _cts.Cancel(); _l.Stop(); }
    }

    private static MatchmakingServer Srv(int port) => new() { Name = "T", Host = "127.0.0.1", Port = port };

    // the probe refuses 127.0.0.1 only in TryCreate (user input); a direct object is fine for tests
    [Fact]
    public async Task Http_status_gives_ping_and_player_counts()
    {
        using var m = new MiniServer(_ => (200, """{"service":"exvs2-matchmaking","version":"t","online":3,"searching":1,"inMatch":2,"matches":1}"""));
        var r = await new ServerProbe(timeout: TimeSpan.FromSeconds(3)).ProbeAsync(Srv(m.Port));
        Assert.Equal("/status", m.LastPath);
        Assert.Equal(ProbeMethod.Http, r.Method);
        Assert.NotNull(r.PingMs);
        Assert.Equal(3, r.Status!.Online);
        Assert.Contains("3 online (1 searching, 2 in match)", r.Summary);
    }

    [Fact]
    public async Task Older_server_without_status_still_gives_a_ping()
    {
        using var m = new MiniServer(_ => (403, ""));
        var r = await new ServerProbe(timeout: TimeSpan.FromSeconds(3)).ProbeAsync(Srv(m.Port));
        Assert.Equal(ProbeMethod.Http, r.Method);
        Assert.NotNull(r.PingMs);
        Assert.Null(r.Status);
        Assert.Contains("player count not available", r.Summary);
    }

    [Fact]
    public async Task Silent_http_falls_back_to_a_tcp_connect()
    {
        using var m = new MiniServer(_ => null);
        var r = await new ServerProbe(timeout: TimeSpan.FromMilliseconds(700)).ProbeAsync(Srv(m.Port));
        Assert.Equal(ProbeMethod.Tcp, r.Method);
        Assert.NotNull(r.PingMs);
        Assert.Contains("(TCP)", r.Summary);
    }

    [Fact]
    public async Task Closed_port_is_offline_and_blank_host_is_not_probed()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        var r = await new ServerProbe(timeout: TimeSpan.FromSeconds(2)).ProbeAsync(Srv(port));
        Assert.False(r.Reachable);
        Assert.StartsWith("offline", r.Summary);
        var none = await new ServerProbe().ProbeAsync(new MatchmakingServer { Name = "LA", Host = "" });
        Assert.Equal("no address yet", none.Summary);
    }
}

using System.Net.NetworkInformation;
using System.Text;
using System.Text.Json;
using Exvs2Launcher.Core.Files;
using Exvs2Launcher.Core.Launch;
using Exvs2Launcher.Core.Net;
using Exvs2Launcher.Core.Profiles;
using Exvs2Launcher.Core.VirtualLan;

namespace Exvs2Launcher.Core.Tests;

internal static class Vlan
{
    public const string Key = "Sup3r-Secret-Key!";
    public const string Community = "exvsclub42";
    public const string Edge = @"C:\n2n\edge.exe";

    public static VirtualLanSettings Settings(Action<VirtualLanSettings>? f = null)
    {
        var s = new VirtualLanSettings
        {
            Enabled = true,
            Supernode = "sn.example.net:7654",
            Community = Community,
            IpMode = VirtualIpMode.Static,
            StaticIp = "10.77.0.11/24",
            EdgePath = Edge,
        };
        f?.Invoke(s);
        return s;
    }

    public static AdapterInfo Tap(bool up = true, params string[] ips) =>
        new("Ethernet 3", "TAP-Windows Adapter V9", up, ips);

    public static AdapterInfo Real(string ip = "192.168.1.80") =>
        new("Ethernet", "Realtek PCIe GbE Family Controller", true, new[] { ip });
}

internal sealed class FakeAdapters : IAdapterSource
{
    public List<AdapterInfo> Now { get; set; } = new();
    /// <summary>After this many List() calls following an edge start, the TAP gets its address (-1 = never).</summary>
    public int ListsUntilIp { get; set; } = 2;
    public string IpWhenUp { get; set; } = "10.77.0.11";
    public bool EdgeRunning { get; set; }
    private int _calls;

    public IReadOnlyList<AdapterInfo> List()
    {
        if (EdgeRunning && ListsUntilIp >= 0 && _calls++ >= ListsUntilIp)
            Now = Now.Select(a => TapAdapters.IsTap(a) ? a with { IsUp = true, IPv4 = new[] { IpWhenUp } } : a).ToList();
        return Now;
    }

    public void EdgeStarted() { EdgeRunning = true; _calls = 0; }
}

internal sealed class FakeEdgeHost : IEdgeHost
{
    private readonly FakeAdapters _adapters;
    public FakeEdgeHost(FakeAdapters adapters) => _adapters = adapters;
    public List<EdgeCommand> Commands { get; } = new();
    public List<FakeProcess> Processes { get; } = new();
    public bool ExitImmediately { get; set; }
    public Exception? Throw { get; set; }
    public int ForceStops { get; private set; }

    public IStartedProcess Start(EdgeCommand command, string logPath)
    {
        if (Throw is not null) throw Throw;
        Commands.Add(command);
        var p = new FakeProcess("edge", 900 + Processes.Count);
        if (ExitImmediately) { p.Exited = true; p.Code = 1; }
        Processes.Add(p);
        _adapters.EdgeStarted();
        return p;
    }

    public string ForceStop(IStartedProcess process)
    {
        ForceStops++;
        process.Kill();
        return "Forced.";
    }
}

internal sealed class FakeMgmt : IEdgeManagement
{
    public EdgePeers Answer { get; set; } = EdgePeers.Unknown;
    public int Stops { get; private set; }
    /// <summary>Process to mark exited when "stop" arrives (null = edge ignores stop).</summary>
    public FakeProcess? StopsProcess { get; set; }
    public bool HonourStop { get; set; } = true;
    public Task<EdgePeers> QueryPeersAsync(int port, CancellationToken ct) => Task.FromResult(Answer);
    public Task RequestStopAsync(int port, CancellationToken ct)
    {
        Stops++;
        if (HonourStop && StopsProcess is not null) StopsProcess.Exited = true;
        return Task.CompletedTask;
    }
}

internal sealed class FakeFiles : IEdgeFiles
{
    public Dictionary<string, string> Written { get; } = new();
    public HashSet<string> Deleted { get; } = new();
    public Queue<string> LogLines { get; } = new();
    public void WritePrivate(string path, string text) { Written[path] = text; Deleted.Remove(path); }
    public void Delete(string path) => Deleted.Add(path);
    public IReadOnlyList<string> ReadNewLines(string path)
    {
        var l = LogLines.ToList();
        LogLines.Clear();
        return l;
    }
    public void ResetLog(string path) { }
}

internal sealed class NoDelay : IDelay
{
    public int Waits { get; private set; }
    public Action? OnWait { get; set; }
    public Task WaitAsync(TimeSpan duration, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        Waits++;
        OnWait?.Invoke();
        return Task.CompletedTask;
    }
}

public class VirtualLanConfigTests
{
    [Fact]
    public void Defaults_are_off_with_research_doc_values()
    {
        var s = new VirtualLanSettings();
        Assert.False(s.Enabled);
        Assert.Equal(1290, s.Mtu);
        Assert.Equal(1, s.Metric);
        Assert.True(s.Multicast);
        Assert.False(s.ForceRelay);
        Assert.Equal(5644, s.ManagementPort);
        Assert.Equal("", s.Supernode);
        Assert.False(new LauncherProfile().VirtualLan.Enabled);
    }

    [Theory]
    [InlineData("sn.example.net:7654", "sn.example.net", 7654)]
    [InlineData(" 203.0.113.5:7777 ", "203.0.113.5", 7777)]
    [InlineData("[2001:db8::1]:7654", "2001:db8::1", 7654)]
    public void Supernode_parses(string text, string host, int port)
    {
        Assert.True(VirtualLanConfig.TryParseSupernode(text, out var a, out var err), err);
        Assert.Equal(host, a!.Host);
        Assert.Equal(port, a.Port);
    }

    [Theory]
    [InlineData("")]
    [InlineData("sn.example.net")]
    [InlineData("sn.example.net:0")]
    [InlineData("sn.example.net:70000")]
    [InlineData("udp://sn.example.net:7654")]
    [InlineData("sn example:7654")]
    [InlineData("2001:db8::1:7654")]
    public void Bad_supernode_is_rejected(string text)
    {
        Assert.False(VirtualLanConfig.TryParseSupernode(text, out _, out var err));
        Assert.False(string.IsNullOrWhiteSpace(err));
    }

    [Theory]
    [InlineData("10.77.0.11", "10.77.0.11/24")]
    [InlineData("10.77.0.11/16", "10.77.0.11/16")]
    [InlineData("static:192.168.100.5/24", "192.168.100.5/24")]
    public void Static_ip_parses_with_default_prefix(string text, string expected)
    {
        Assert.True(VirtualLanConfig.TryParseStatic(text, out var a, out var err), err);
        Assert.Equal(expected, a!.ToString());
    }

    [Theory]
    [InlineData("")]
    [InlineData("10.77.0")]
    [InlineData("10.77.0.0/24")]
    [InlineData("10.77.0.255/24")]
    [InlineData("127.0.0.1")]
    [InlineData("169.254.3.4")]
    [InlineData("10.77.0.11/31")]
    [InlineData("::1")]
    public void Bad_static_ip_is_rejected(string text) =>
        Assert.False(VirtualLanConfig.TryParseStatic(text, out _, out _));

    [Fact]
    public void Validate_passes_good_settings()
    {
        var errors = VirtualLanConfig.Validate(Vlan.Settings(), Vlan.Key, p => p == Vlan.Edge);
        Assert.Empty(errors);
    }

    [Fact]
    public void Validate_reports_every_problem_without_the_key()
    {
        var s = Vlan.Settings(x =>
        {
            x.Supernode = "";
            x.Community = "this-community-name-is-way-too-long";
            x.StaticIp = "nope";
            x.Mtu = 9000;
            x.EdgePath = @"C:\n2n\missing.exe";
        });
        var errors = VirtualLanConfig.Validate(s, " " + Vlan.Key, _ => false);
        Assert.Equal(6, errors.Count);
        Assert.DoesNotContain(errors, e => e.Contains(Vlan.Key));
        Assert.Contains(errors, e => e.Contains("edge.exe was not found"));
        Assert.Contains(errors, e => e.Contains("too long"));
    }

    [Fact]
    public void Validate_requires_a_key_and_edge()
    {
        var errors = VirtualLanConfig.Validate(Vlan.Settings(x => x.EdgePath = ""), null, _ => true);
        Assert.Contains(errors, e => e.Contains("encryption key"));
        Assert.Contains(errors, e => e.Contains("does not ship"));
    }

    [Fact]
    public void Dhcp_and_supernode_modes_do_not_need_a_static_ip()
    {
        foreach (var mode in new[] { VirtualIpMode.Dhcp, VirtualIpMode.SupernodeAssigned })
            Assert.Empty(VirtualLanConfig.Validate(Vlan.Settings(x => { x.IpMode = mode; x.StaticIp = ""; }), Vlan.Key, _ => true));
    }

    [Fact]
    public void Old_profile_json_without_virtual_lan_loads_with_it_off()
    {
        var dir = Path.Combine(Path.GetTempPath(), "exvs2-vlan-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var path = Path.Combine(dir, ProfileStore.FileName);
            File.WriteAllText(path, "{\"Version\":1,\"Profiles\":[{\"Name\":\"Cab\",\"GameFolder\":\"C:\\\\g\"}]}");
            var f = new ProfileStore(path).Load();
            Assert.False(f.Profiles[0].VirtualLan.Enabled);
            Assert.Equal(1290, f.Profiles[0].VirtualLan.Mtu);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void Saved_profile_never_contains_the_key()
    {
        var dir = Path.Combine(Path.GetTempPath(), "exvs2-vlan-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var path = Path.Combine(dir, ProfileStore.FileName);
            var p = new LauncherProfile { Name = "Cab", VirtualLan = Vlan.Settings() };
            new ProfileStore(path).Save(new ProfileFile { Profiles = { p } });
            var keys = new VirtualLanKeyStore(Path.Combine(dir, VirtualLanKeyStore.FileName), Xor, Xor);
            keys.Set("Cab", Vlan.Key);
            Assert.DoesNotContain(Vlan.Key, File.ReadAllText(path));
            Assert.DoesNotContain(Vlan.Community, File.ReadAllText(path));
            Assert.DoesNotContain("\"Key\"", File.ReadAllText(path));
            Assert.DoesNotContain(Vlan.Key, Encoding.UTF8.GetString(File.ReadAllBytes(keys.FilePath)));
        }
        finally { Directory.Delete(dir, true); }
    }

    internal static byte[] Xor(byte[] b) => b.Select(x => (byte)(x ^ 0x5A)).ToArray();
}

public class VirtualLanKeyStoreTests
{
    [Fact]
    public void Round_trips_renames_and_clears()
    {
        var path = Path.Combine(Path.GetTempPath(), "exvs2-keys-" + Guid.NewGuid().ToString("N") + ".dat");
        try
        {
            var s = new VirtualLanKeyStore(path, VirtualLanConfigTests.Xor, VirtualLanConfigTests.Xor);
            Assert.Null(s.Get("Cab"));
            s.Set("Cab", Vlan.Key);
            Assert.Equal(Vlan.Key, new VirtualLanKeyStore(path, VirtualLanConfigTests.Xor, VirtualLanConfigTests.Xor).Get("Cab"));
            s.SetCommunity("Cab", Vlan.Community);
            Assert.Equal(Vlan.Community, s.GetCommunity("Cab"));
            Assert.Null(s.Get("Cab 2"));
            s.Rename("Cab", "Cab 2");
            Assert.Null(s.Get("Cab"));
            Assert.Null(s.GetCommunity("Cab"));
            Assert.True(s.Has("Cab 2"));
            Assert.Equal(Vlan.Community, s.GetCommunity("Cab 2"));
            s.Set("Cab 2", null);
            s.SetCommunity("Cab 2", null);
            Assert.False(File.Exists(path));
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public void Unreadable_file_acts_as_no_key()
    {
        var path = Path.Combine(Path.GetTempPath(), "exvs2-keys-" + Guid.NewGuid().ToString("N") + ".dat");
        try
        {
            File.WriteAllBytes(path, new byte[] { 1, 2, 3 });
            var s = new VirtualLanKeyStore(path, b => b, _ => throw new InvalidOperationException("other user"));
            Assert.Null(s.Get("Cab"));
        }
        finally { File.Delete(path); }
    }
}

public class EdgeCommandTests
{
    private const string Conf = @"C:\Users\me\AppData\Local\exvs2-launcher\n2n\edge.conf";

    [Fact]
    public void Static_mode_config_has_every_option()
    {
        var c = EdgeCommand.Build(Vlan.Settings(), Vlan.Key, Conf);
        var lines = c.ConfigFileText().Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(new[]
        {
            "-c=" + Vlan.Community, "-k=" + Vlan.Key, "-l=sn.example.net:7654", "-a=10.77.0.11/24",
            "-M=1290", "-x=1", "-E", "-t=5644",
        }, lines);
        Assert.Equal(Vlan.Edge, c.ExePath);
    }

    [Fact]
    public void Key_and_community_never_appear_in_anything_logged()
    {
        var c = EdgeCommand.Build(Vlan.Settings(x => { x.ForceRelay = true; x.HeaderEncryption = true; }), Vlan.Key, Conf);
        foreach (var logged in new[] { c.DisplayCommandLine, c.Arguments, c.RedactedArgumentString(), c.RedactedConfigText() })
        {
            Assert.DoesNotContain(Vlan.Key, logged);
            Assert.DoesNotContain(Vlan.Community, logged);
        }
        Assert.Equal("\"" + Vlan.Edge + "\" \"" + Conf + "\"", c.DisplayCommandLine);
        Assert.Contains("-k (hidden)", c.RedactedArgumentString().Replace("\"", ""));
        Assert.Contains("-S1", c.RedactedArgumentString());
        Assert.Contains("-H", c.RedactedArgumentString());
        Assert.Contains(Vlan.Key, c.ConfigFileText());
    }

    [Fact]
    public void Dhcp_mode_uses_r_and_dhcp_address()
    {
        var c = EdgeCommand.Build(Vlan.Settings(x => x.IpMode = VirtualIpMode.Dhcp), Vlan.Key, Conf);
        var text = c.ConfigFileText();
        Assert.Contains("-r\n", text);
        Assert.Contains("-a=dhcp:0.0.0.0\n", text);
    }

    [Fact]
    public void Supernode_assigned_mode_omits_a()
    {
        var c = EdgeCommand.Build(Vlan.Settings(x => x.IpMode = VirtualIpMode.SupernodeAssigned), Vlan.Key, Conf);
        Assert.DoesNotContain(c.Options, o => o.Flag == "-a");
    }

    [Fact]
    public void Optional_flags_follow_settings()
    {
        var c = EdgeCommand.Build(Vlan.Settings(x =>
        {
            x.Multicast = false; x.ForceRelay = true; x.Mtu = 1400; x.Metric = 5; x.ManagementPort = 5650;
            x.AdapterName = "n2n TAP"; x.Description = "cab1";
        }), Vlan.Key, Conf);
        var text = c.ConfigFileText();
        Assert.DoesNotContain("-E\n", text);
        Assert.Contains("-S1\n", text);
        Assert.Contains("-M=1400\n", text);
        Assert.Contains("-x=5\n", text);
        Assert.Contains("-t=5650\n", text);
        Assert.Contains("-d=n2n TAP\n", text);
        Assert.Contains("-I=cab1\n", text);
    }

    [Fact]
    public void Redact_replaces_every_secret()
    {
        var s = EdgeCommand.Redact($"community '{Vlan.Community}' key {Vlan.Key} again {Vlan.Key}", Vlan.Key, Vlan.Community, null);
        Assert.Equal("community '(hidden)' key (hidden) again (hidden)", s);
    }

    [Fact]
    public void Build_refuses_bad_settings()
    {
        Assert.Throws<ArgumentException>(() => EdgeCommand.Build(Vlan.Settings(x => x.Supernode = "nope"), Vlan.Key, Conf));
        Assert.Throws<ArgumentException>(() => EdgeCommand.Build(Vlan.Settings(), "", Conf));
    }
}

public class TapAdapterTests
{
    [Fact]
    public void No_tap_adapter_explains_the_driver_and_links_the_official_page()
    {
        var sel = TapAdapters.Select(new[] { Vlan.Real() }, Vlan.Settings());
        Assert.Null(sel.Ip);
        Assert.Contains("TAP-Windows", sel.Problem);
        Assert.Contains("https://build.openvpn.net/downloads/releases/", sel.Problem);
        Assert.Contains("does not bundle", sel.Problem);
    }

    [Fact]
    public void Picks_the_tap_address_never_the_real_nic()
    {
        var sel = TapAdapters.Select(new[] { Vlan.Real(), Vlan.Tap(true, "10.77.0.11") }, Vlan.Settings());
        Assert.Equal("10.77.0.11", sel.Ip);
        Assert.Equal("Ethernet 3", sel.Adapter!.Name);
    }

    [Fact]
    public void Down_or_link_local_tap_is_not_ready()
    {
        Assert.Null(TapAdapters.Select(new[] { Vlan.Real(), Vlan.Tap(false, "10.77.0.11") }, Vlan.Settings()).Ip);
        Assert.Null(TapAdapters.Select(new[] { Vlan.Real(), Vlan.Tap(true, "169.254.10.1") }, Vlan.Settings(x => x.IpMode = VirtualIpMode.Dhcp)).Ip);
    }

    [Fact]
    public void Static_mode_waits_for_the_exact_address()
    {
        var sel = TapAdapters.Select(new[] { Vlan.Tap(true, "10.77.0.99") }, Vlan.Settings());
        Assert.Null(sel.Ip);
        Assert.Contains("10.77.0.11", sel.Problem);
        var any = TapAdapters.Select(new[] { Vlan.Tap(true, "10.128.0.7") }, Vlan.Settings(x => x.IpMode = VirtualIpMode.SupernodeAssigned));
        Assert.Equal("10.128.0.7", any.Ip);
    }

    [Fact]
    public void Two_taps_with_addresses_need_an_adapter_name()
    {
        var a = Vlan.Tap(true, "10.77.0.11");
        var b = new AdapterInfo("OpenVPN TAP", "TAP-Windows Adapter V9 #2", true, new[] { "10.8.0.2" });
        var sel = TapAdapters.Select(new[] { a, b }, Vlan.Settings(x => x.IpMode = VirtualIpMode.SupernodeAssigned));
        Assert.Null(sel.Ip);
        Assert.Contains("More than one TAP", sel.Problem);
        var named = TapAdapters.Select(new[] { a, b }, Vlan.Settings(x => { x.IpMode = VirtualIpMode.SupernodeAssigned; x.AdapterName = "OpenVPN TAP"; }));
        Assert.Equal("10.8.0.2", named.Ip);
    }

    [Fact]
    public void Adapter_name_never_selects_a_real_nic()
    {
        var sel = TapAdapters.Select(new[] { Vlan.Real(), Vlan.Tap(true, "10.77.0.11") },
            Vlan.Settings(x => { x.AdapterName = "Ethernet"; x.IpMode = VirtualIpMode.SupernodeAssigned; }));
        Assert.Null(sel.Ip);
        Assert.Contains("No TAP-Windows adapter named", sel.Problem);
    }

    [Fact]
    public void Internet_mode_detector_still_never_auto_picks_the_tap()
    {
        var real = new NicAddress("Ethernet", "Realtek", "192.168.1.80", NetworkInterfaceType.Ethernet, true, false);
        var tap = new NicAddress("Ethernet 3", "TAP-Windows Adapter V9", "10.77.0.11", NetworkInterfaceType.Ethernet, true,
            LanAddressDetector.LooksVirtual("Ethernet 3", "TAP-Windows Adapter V9"));
        Assert.True(tap.IsVirtual);
        Assert.Equal("192.168.1.80", LanAddressDetector.PickBest(new[] { tap, real })!.Address);
    }
}

public class EdgeStatusParserTests
{
    [Fact]
    public void Parses_json_rows()
    {
        var packets = new[]
        {
            "{\"_tag\":\"1\",\"_type\":\"begin\",\"cmd\":\"edges\"}",
            "{\"_tag\":\"1\",\"_type\":\"row\",\"mode\":\"pSp\",\"ip4addr\":\"10.77.0.12\"}",
            "{\"_tag\":\"1\",\"_type\":\"row\",\"mode\":\"p2p\",\"ip4addr\":\"10.77.0.13\"}",
            "{\"_tag\":\"1\",\"_type\":\"row\",\"mode\":\"p2p\",\"ip4addr\":\"10.77.0.14\"}",
            "{\"_tag\":\"1\",\"_type\":\"end\",\"cmd\":\"edges\"}",
        };
        var p = EdgeStatusParser.Parse(packets);
        Assert.True(p.Parsed);
        Assert.Equal(2, p.Direct);
        Assert.Equal(1, p.Relayed);
        Assert.Equal("3 peer(s): 2 direct, 1 via supernode", p.Describe());
    }

    [Fact]
    public void Parses_n2n_3_0_text_table()
    {
        var text = "COMMUNITY '-- header encrypted --'\n\n ### | TAP | MAC | EDGE | HINT | LAST SEEN | UPTIME\n" +
                   "=====\nSUPERNODE FORWARD\n   1 | 10.77.0.12 | 0a:1b:2c:3d:4e:5f | 203.0.113.9:50001 | cab2 |  3 |\n" +
                   "-----\nPEER TO PEER\n   1 | 10.77.0.13 | 0a:1b:2c:3d:4e:60 | 198.51.100.4:40000 | cab3 | 1 |\n" +
                   "-----\nSUPERNODES\n3.0.0 l* | 0a:00:00:00:00:01 | 192.0.2.1:7654 | ld = 0 | 1 | 100\n";
        var p = EdgeStatusParser.Parse(new[] { text });
        Assert.True(p.Parsed);
        Assert.Equal(1, p.Direct);
        Assert.Equal(1, p.Relayed);
    }

    [Fact]
    public void Empty_or_error_reply_is_unknown()
    {
        Assert.False(EdgeStatusParser.Parse(Array.Empty<string>()).Parsed);
        Assert.False(EdgeStatusParser.Parse(new[] { "{\"_tag\":\"1\",\"_type\":\"error\",\"error\":\"badauth\"}" }).Parsed);
        Assert.Equal("no peers yet", EdgeStatusParser.Parse(new[] { "{\"_tag\":\"1\",\"_type\":\"begin\"}", "{\"_tag\":\"1\",\"_type\":\"end\"}" }).Describe());
    }
}

public class VirtualLanControllerTests
{
    private static (VirtualLanController, FakeAdapters, FakeEdgeHost, FakeMgmt, FakeFiles, List<string>, List<VirtualLanState>) Make(Action<FakeAdapters>? setup = null)
    {
        var adapters = new FakeAdapters { Now = new() { Vlan.Real(), Vlan.Tap(false) } };
        setup?.Invoke(adapters);
        var host = new FakeEdgeHost(adapters);
        var mgmt = new FakeMgmt();
        var files = new FakeFiles();
        var c = new VirtualLanController(host, adapters, mgmt, new NoDelay(), @"C:\work", files, p => p == Vlan.Edge);
        var log = new List<string>();
        var states = new List<VirtualLanState>();
        c.Log += log.Add;
        c.StateChanged += states.Add;
        return (c, adapters, host, mgmt, files, log, states);
    }

    private static void AssertNoSecrets(IEnumerable<string> log)
    {
        foreach (var l in log)
        {
            Assert.DoesNotContain(Vlan.Key, l);
            Assert.DoesNotContain(Vlan.Community, l);
        }
    }

    [Fact]
    public async Task Start_goes_starting_waiting_up_and_deletes_the_config()
    {
        var (c, _, host, _, files, log, states) = Make();
        files.LogLines.Enqueue($"02/Nov/2026 edge started, community '{Vlan.Community}' key {Vlan.Key}");
        var r = await c.StartAsync(Vlan.Settings(), Vlan.Key);

        Assert.True(r.Succeeded, r.Message);
        Assert.Equal("10.77.0.11", r.Ip);
        Assert.Equal(new[] { VirtualLanState.Starting, VirtualLanState.WaitingForAdapter, VirtualLanState.Up }, states);
        Assert.Single(host.Commands);
        Assert.Contains("-k=" + Vlan.Key, files.Written[c.ConfigPath]);
        Assert.Contains(c.ConfigPath, files.Deleted);
        Assert.Contains(log, l => l.StartsWith("edge: ") && l.Contains("(hidden)"));
        AssertNoSecrets(log);
        Assert.Contains("10.77.0.11", c.Status.Describe());
    }

    [Fact]
    public async Task Missing_tap_driver_fails_before_asking_for_admin()
    {
        var (c, _, host, _, _, log, states) = Make(a => a.Now = new() { Vlan.Real() });
        var r = await c.StartAsync(Vlan.Settings(), Vlan.Key);
        Assert.False(r.Succeeded);
        Assert.Contains("TAP-Windows", r.Message);
        Assert.Empty(host.Commands);
        Assert.Equal(VirtualLanState.Failed, states.Last());
    }

    [Fact]
    public async Task Invalid_settings_never_start_edge()
    {
        var (c, _, host, _, _, log, _) = Make();
        var r = await c.StartAsync(Vlan.Settings(x => x.Supernode = ""), Vlan.Key);
        Assert.False(r.Succeeded);
        Assert.Empty(host.Commands);
        AssertNoSecrets(log);
    }

    [Fact]
    public async Task Edge_exiting_early_fails_with_a_clear_message()
    {
        var (c, _, host, _, files, log, _) = Make();
        host.ExitImmediately = true;
        var r = await c.StartAsync(Vlan.Settings(), Vlan.Key);
        Assert.False(r.Succeeded);
        Assert.Contains("exited (code 1)", r.Message);
        Assert.Equal(VirtualLanState.Failed, c.State);
        Assert.Contains(c.ConfigPath, files.Deleted);
        AssertNoSecrets(log);
    }

    [Fact]
    public async Task Timeout_stops_edge_and_fails()
    {
        var (c, adapters, host, mgmt, files, log, _) = Make(a => a.ListsUntilIp = -1);
        var settings = Vlan.Settings(x => x.StartTimeoutSeconds = 5);
        mgmt.StopsProcess = null;
        var task = c.StartAsync(settings, Vlan.Key);
        var r = await task;
        Assert.False(r.Succeeded);
        Assert.Contains("did not get a virtual IP within 5 s", r.Message);
        Assert.Equal(1, mgmt.Stops);
        Assert.Equal(1, host.ForceStops); // edge ignored stop in this fake
        Assert.Contains(c.ConfigPath, files.Deleted);
        Assert.Null(c.Ip);
    }

    [Fact]
    public async Task Uac_cancel_fails_cleanly()
    {
        var (c, _, host, _, files, _, _) = Make();
        host.Throw = new InvalidOperationException("The operation was canceled by the user.");
        var r = await c.StartAsync(Vlan.Settings(), Vlan.Key);
        Assert.False(r.Succeeded);
        Assert.Contains("administrator", r.Message);
        Assert.Contains(c.ConfigPath, files.Deleted);
    }

    [Fact]
    public async Task Second_start_reuses_the_running_edge()
    {
        var (c, _, host, _, _, _, _) = Make();
        await c.StartAsync(Vlan.Settings(), Vlan.Key);
        var r = await c.StartAsync(Vlan.Settings(), Vlan.Key);
        Assert.True(r.Succeeded);
        Assert.Contains("already up", r.Message);
        Assert.Single(host.Commands);
    }

    [Fact]
    public async Task Edge_that_died_is_restarted_on_next_start()
    {
        var (c, _, host, _, _, _, _) = Make();
        await c.StartAsync(Vlan.Settings(), Vlan.Key);
        host.Processes[0].Exited = true;
        var r = await c.StartAsync(Vlan.Settings(), Vlan.Key);
        Assert.True(r.Succeeded, r.Message);
        Assert.Equal(2, host.Commands.Count);
    }

    [Fact]
    public async Task Stop_is_graceful_through_the_management_port()
    {
        var (c, _, host, mgmt, _, log, states) = Make();
        await c.StartAsync(Vlan.Settings(), Vlan.Key);
        mgmt.StopsProcess = host.Processes[0];
        var note = await c.StopAsync();
        Assert.Contains("cleanly", note);
        Assert.Equal(1, mgmt.Stops);
        Assert.Equal(0, host.ForceStops);
        Assert.False(host.Processes[0].Killed);
        Assert.Equal(VirtualLanState.Off, c.State);
        Assert.Equal(new[] { VirtualLanState.Stopping, VirtualLanState.Off }, states.Skip(3));
        Assert.Null(c.Ip);
    }

    [Fact]
    public async Task Stop_forces_only_when_edge_ignores_stop()
    {
        var (c, _, host, mgmt, _, _, _) = Make();
        await c.StartAsync(Vlan.Settings(), Vlan.Key);
        mgmt.HonourStop = false;
        mgmt.StopsProcess = host.Processes[0];
        var note = await c.StopAsync();
        Assert.Contains("did not stop", note);
        Assert.Equal(1, host.ForceStops);
    }

    [Fact]
    public async Task Edge_already_running_elsewhere_is_used_but_not_stopped()
    {
        var (c, _, host, mgmt, _, log, _) = Make(a => a.Now = new() { Vlan.Real(), Vlan.Tap(true, "10.77.0.11") });
        mgmt.Answer = new EdgePeers(1, 0, true);
        var r = await c.StartAsync(Vlan.Settings(), Vlan.Key);
        Assert.True(r.Succeeded);
        Assert.Empty(host.Commands);
        Assert.False(c.StartedByLauncher);
        var note = await c.StopAsync();
        Assert.Contains("left running", note);
        Assert.Equal(0, mgmt.Stops);
    }

    [Fact]
    public async Task Refresh_reads_peers_and_notices_edge_exit()
    {
        var (c, _, host, mgmt, _, _, _) = Make();
        await c.StartAsync(Vlan.Settings(), Vlan.Key);
        mgmt.Answer = new EdgePeers(2, 1, true);
        var s = await c.RefreshAsync();
        Assert.Equal(3, s.Peers.Total);
        host.Processes[0].Exited = true;
        s = await c.RefreshAsync();
        Assert.Equal(VirtualLanState.Failed, s.State);
        Assert.Contains("exited", s.LastError);
    }

    [Fact]
    public async Task Cancel_stops_edge()
    {
        var (c, adapters, host, mgmt, _, _, _) = Make(a => a.ListsUntilIp = -1);
        using var cts = new CancellationTokenSource();
        var delay = new NoDelay();
        var c2 = new VirtualLanController(host, adapters, mgmt, delay, @"C:\work", new FakeFiles(), p => p == Vlan.Edge);
        delay.OnWait = () => { if (delay.Waits == 3) cts.Cancel(); };
        mgmt.StopsProcess = null;
        var r = await c2.StartAsync(Vlan.Settings(), Vlan.Key, cts.Token);
        Assert.False(r.Succeeded);
        Assert.Equal("Cancelled.", r.Message);
        Assert.Equal(VirtualLanState.Off, c2.State);
        Assert.Equal(1, mgmt.Stops);
    }
}

public class CabLauncherVirtualLanTests
{
    private static readonly NicAddress Lan = new("Ethernet", "Realtek", "192.168.1.80", NetworkInterfaceType.Ethernet, true, false);
    private static readonly NicAddress ExistingLm = new("Ethernet", "Realtek", "192.168.1.69", NetworkInterfaceType.Ethernet, true, false);

    private static (CabLauncher, FakeWorld, FakeEdgeHost, FakeAdapters, List<string>) Make(TempGameFolder g, Action<FakeAdapters>? setup = null)
    {
        var w = new FakeWorld();
        w.Files.Add(Path.Combine(g.Root, "3-Start-Server.bat"));
        w.Files.Add(Path.Combine(g.Root, "vsac29_Release.exe"));
        w.Files.Add(Path.Combine(g.Root, "vsac29_Release_LM.exe"));
        w.Files.Add(Vlan.Edge);
        var seq = new StartSequencer(w, w, w, w, w.Files.Contains);
        var adapters = new FakeAdapters { Now = new() { Vlan.Real(), Vlan.Tap(false) } };
        setup?.Invoke(adapters);
        var host = new FakeEdgeHost(adapters);
        var vlan = new VirtualLanController(host, adapters, new FakeMgmt(), new NoDelay(), g.Root, new FakeFiles(), w.Files.Contains);
        var launcher = new CabLauncher(seq, new GameConfigWriter(), () => Array.Empty<string>(),
            () => new[] { Lan, ExistingLm }, vlan, _ => Vlan.Key);
        var log = new List<string>();
        launcher.Log += log.Add;
        seq.Log += log.Add;
        vlan.Log += log.Add;
        return (launcher, w, host, adapters, log);
    }

    private static LauncherProfile Profile(TempGameFolder g, bool vlan) => new()
    {
        Name = "Test",
        GameFolder = g.Root,
        CardServerPath = Path.Combine(g.Root, "3-Start-Server.bat"),
        StartLm = true,
        LmToClientDelaySeconds = 0,
        PcbIp = "192.168.1.80",
        LmIp = "192.168.1.69",
        VirtualLan = Vlan.Settings(x => x.Enabled = vlan),
    };

    [Fact]
    public async Task Start_all_brings_up_edge_first_and_writes_the_tap_ip()
    {
        using var g = new TempGameFolder();
        var (launcher, w, host, _, log) = Make(g);
        var p = Profile(g, vlan: true);
        var r = await launcher.StartAsync(new ProfileFile { Profiles = { p } }, p);

        Assert.True(r.Succeeded, r.Message);
        Assert.Single(host.Commands);
        Assert.Contains("IpAddress=10.77.0.11", g.Text("config.ini"));
        Assert.Contains("IpAddress=10.77.0.11", g.Text("lmnfig.ini"));
        Assert.Contains("Server =127.0.0.1:28080", g.Text("config.ini"));
        Assert.Contains("Server=127.0.0.1:28080", g.Text("lmnfig.ini"));
        // Profile LAN IPs are kept so turning virtual LAN off goes back to them.
        Assert.Equal("192.168.1.80", p.PcbIp);
        Assert.Equal("192.168.1.69", p.LmIp);
        Assert.Equal("10.77.0.11", p.VirtualLan.LastVirtualIp);
        Assert.Equal(new[] { "Card server", "LM", "Client" }, r.Started.Select(x => x.Role));
        int vlanUp = log.FindIndex(l => l.Contains("Virtual LAN up"));
        int card = log.FindIndex(l => l.Contains("Card server is listening"));
        Assert.True(vlanUp >= 0 && vlanUp < card);
        foreach (var l in log) Assert.DoesNotContain(Vlan.Key, l);
    }

    [Fact]
    public async Task Virtual_lan_failure_starts_nothing()
    {
        using var g = new TempGameFolder();
        var (launcher, w, host, _, _) = Make(g, a => a.Now = new() { Vlan.Real() });
        var p = Profile(g, vlan: true);
        var r = await launcher.StartAsync(new ProfileFile { Profiles = { p } }, p);
        Assert.False(r.Succeeded);
        Assert.Contains("TAP-Windows", r.Message);
        Assert.Empty(w.Processes);
        Assert.Contains("IpAddress=192.168.1.69", g.Text("config.ini"));
    }

    [Fact]
    public async Task Virtual_lan_off_never_touches_edge_and_undoes_the_tap_ip()
    {
        using var g = new TempGameFolder();
        var (launcher, _, host, adapters, log) = Make(g, a => a.Now = new() { Vlan.Real(), Vlan.Tap(true, "10.77.0.11") });
        File.WriteAllText(g.P("config.ini"), TempGameFolder.Config.Replace("IpAddress=192.168.1.69", "IpAddress=10.77.0.11"));
        File.WriteAllText(g.P("lmnfig.ini"), TempGameFolder.Lm.Replace("192.168.1.69", "10.77.0.11"));
        var p = Profile(g, vlan: false);
        p.VirtualLan.LastVirtualIp = "10.77.0.11";
        var r = await launcher.StartAsync(new ProfileFile { Profiles = { p } }, p);

        Assert.True(r.Succeeded, r.Message);
        Assert.Empty(host.Commands);
        Assert.Contains("IpAddress=192.168.1.80", g.Text("config.ini"));
        Assert.Contains("IpAddress=192.168.1.69", g.Text("lmnfig.ini"));
        Assert.Null(p.VirtualLan.LastVirtualIp);
        Assert.Contains(log, l => l.Contains("old virtual LAN address"));
    }

    [Fact]
    public async Task Start_lm_alone_in_virtual_lan_mode_writes_only_lmnfig()
    {
        using var g = new TempGameFolder();
        var (launcher, w, host, _, _) = Make(g);
        var p = Profile(g, vlan: true);
        var r = await launcher.StartRoleAsync(p, ComponentRoles.Lm);
        Assert.True(r.Succeeded, r.Message);
        Assert.Single(host.Commands);
        Assert.Contains("IpAddress=10.77.0.11", g.Text("lmnfig.ini"));
        Assert.Contains("IpAddress=192.168.1.69", g.Text("config.ini"));
    }

    [Fact]
    public async Task Start_card_server_alone_does_not_need_the_virtual_lan()
    {
        using var g = new TempGameFolder();
        var (launcher, _, host, _, _) = Make(g, a => a.Now = new() { Vlan.Real() });
        var p = Profile(g, vlan: true);
        var r = await launcher.StartRoleAsync(p, ComponentRoles.CardServer);
        Assert.True(r.Succeeded, r.Message);
        Assert.Empty(host.Commands);
    }

    [Fact]
    public async Task Hook_off_is_still_never_written_and_is_warned_in_virtual_lan_mode()
    {
        using var g = new TempGameFolder();
        File.WriteAllText(g.P("lmnfig.ini"), TempGameFolder.Lm.Replace("[display]", "DisableSocketHook=True\r\n[display]"));
        var (launcher, _, _, _, log) = Make(g);
        var p = Profile(g, vlan: true);
        var r = await launcher.StartAsync(new ProfileFile { Profiles = { p } }, p);
        Assert.True(r.Succeeded, r.Message);
        Assert.Contains(log, l => l.Contains("DisableSocketHook=True") && l.Contains("21-32"));
    }
}

using System.Net;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Text;
using Exvs2Launcher.Core.Files;
using Exvs2Launcher.Core.Identity;
using Exvs2Launcher.Core.Launch;
using Exvs2Launcher.Core.Net;
using Exvs2Launcher.Core.Profiles;
using Exvs2Launcher.Core.Wizard;

namespace Exvs2Launcher.Core.Tests;

public class LoopbackAndSecondaryAddressTests
{
    private static readonly NicAddress Primary = new("Ethernet", "Realtek", "192.168.1.69", NetworkInterfaceType.Ethernet, true, false);
    private static readonly NicAddress Secondary = new("Ethernet", "Realtek", "192.168.1.245", NetworkInterfaceType.Ethernet, true, false);
    private static readonly IReadOnlyList<NicAddress> Two = new[] { Primary, Secondary };
    private static readonly IReadOnlyList<NicAddress> One = new[] { Primary };

    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("127.0.0.2")]
    [InlineData("::1")]
    [InlineData("localhost")]
    [InlineData("LOCALHOST")]
    public void Detects_loopback(string ip) => Assert.True(LoopbackAddress.IsLoopback(ip));

    [Theory]
    [InlineData("192.168.1.245")]
    [InlineData("10.10.0.2")]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("0.0.0.0")]
    public void Non_loopback_or_empty(string? ip) => Assert.False(LoopbackAddress.IsLoopback(ip));

    [Fact]
    public void Admin_command_format()
    {
        Assert.Equal(
            "New-NetIPAddress -InterfaceAlias 'Ethernet' -IPAddress '192.168.1.245' -PrefixLength 24 -SkipAsSource $true",
            SecondaryAddress.NewNetIpAddressCommand("Ethernet", "192.168.1.245"));
        Assert.Equal(
            "New-NetIPAddress -InterfaceAlias 'Foo''s NIC' -IPAddress '10.0.0.5' -PrefixLength 16 -SkipAsSource $true",
            SecondaryAddress.NewNetIpAddressCommand("Foo's NIC", "10.0.0.5", 16));
    }

    [Fact]
    public void Suggests_245_when_free_and_skips_used_addresses()
    {
        Assert.Equal("192.168.1.245", SecondaryAddress.Suggest("192.168.1.69", Array.Empty<string>()));
        Assert.Equal("192.168.1.240", SecondaryAddress.Suggest("192.168.1.69", new[] { "192.168.1.245" }));
        Assert.Null(SecondaryAddress.Suggest("not-an-ip", Array.Empty<string>()));
    }

    [Fact]
    public void Advise_picks_existing_second_address_on_same_adapter()
    {
        var a = SecondaryAddress.Advise("192.168.1.69", Two);
        Assert.True(a.HasSecondAddressOnAdapter);
        Assert.Equal("192.168.1.245", a.SuggestedIp);
        Assert.Null(a.Command);
        Assert.Contains("already has another IPv4", a.Message);
    }

    [Fact]
    public void Advise_shows_command_when_adapter_has_only_pcb_ip()
    {
        var a = SecondaryAddress.Advise("192.168.1.69", One);
        Assert.False(a.HasSecondAddressOnAdapter);
        Assert.Equal("192.168.1.245", a.SuggestedIp);
        Assert.Equal(
            "New-NetIPAddress -InterfaceAlias 'Ethernet' -IPAddress '192.168.1.245' -PrefixLength 24 -SkipAsSource $true",
            a.Command);
        Assert.Contains("SkipAsSource", a.Message);
    }

    [Fact]
    public void Single_cab_may_share_pcb_ip_multi_cab_asks_for_a_second()
    {
        var (lm, msgs) = SecondaryAddress.ChooseLm(onlyCabOnThisPc: true, "192.168.1.69", null, One);
        Assert.Equal("192.168.1.69", lm);
        Assert.Contains(msgs, m => m.Contains("Single cab"));

        var (lm2, msgs2) = SecondaryAddress.ChooseLm(onlyCabOnThisPc: false, "192.168.1.69", "192.168.1.69", Two);
        Assert.Equal("192.168.1.69", lm2);
        Assert.Contains(msgs2, m => m.Contains("more than one cab") && m.Contains("second address"));
    }

    [Fact]
    public void ChooseLm_rejects_loopback()
    {
        var (lm, msgs) = SecondaryAddress.ChooseLm(true, "192.168.1.69", "127.0.0.1", One);
        Assert.Null(lm);
        Assert.Contains(msgs, m => m.Contains("21-01"));
    }

    [Fact]
    public void NetworkBinding_does_not_keep_loopback_lmnfig_ip()
    {
        var d = NetworkBinding.Resolve(
            currentPcbIp: "192.168.1.69",
            currentLmIp: "127.0.0.1",
            profilePcbIp: null,
            profileLmIp: "127.0.0.1",
            autoDetect: true,
            nics: Two);
        Assert.Null(d.LmIp);
        Assert.False(d.KeptExistingLmIp);
        Assert.Contains(d.Messages, m => m.Contains("loopback") && m.Contains("will not be kept"));
        Assert.DoesNotContain(d.Messages, m => m.Contains("left as 127.0.0.1") && m.Contains("authoritative"));
    }
}

public class GameFolderValidatorTests
{
    [Fact]
    public void Missing_folder_is_blocking()
    {
        var c = GameFolderValidator.Check("");
        Assert.False(c.CanContinue);
        Assert.Contains(c.Issues, i => i.Blocking);
    }

    [Fact]
    public void Requires_exes_dll_and_inis_and_warns_on_shared_folder()
    {
        using var g = new TempGameFolder();
        var missing = GameFolderValidator.Check(g.Root);
        Assert.False(missing.CanContinue);
        Assert.Contains(missing.Issues, i => i.Code == "client-exe" && i.Blocking);
        Assert.Contains(missing.Issues, i => i.Code == "lm-exe");
        Assert.Contains(missing.Issues, i => i.Code == "bngrw");

        TouchGameBits(g);
        var ok = GameFolderValidator.Check(g.Root);
        Assert.True(ok.CanContinue);
        Assert.True(ok.HasCardIni);

        var file = new ProfileFile();
        file.Profiles.Add(new LauncherProfile { Name = "Cab A", GameFolder = g.Root });
        var warn = GameFolderValidator.Check(g.Root, file, currentProfileName: "New cab");
        Assert.True(warn.CanContinue);
        Assert.Equal("Cab A", warn.UsedByProfile);
        Assert.Contains(warn.Issues, i => i.Code == "in-use" && !i.Blocking);
    }

    internal static void TouchGameBits(TempGameFolder g)
    {
        File.WriteAllBytes(g.P(GameFolder.ClientExeName), new byte[] { 0x4D, 0x5A });
        File.WriteAllBytes(g.P(GameFolder.LmExeName), new byte[] { 0x4D, 0x5A });
        File.WriteAllBytes(g.P(GameFolder.BngrwDllName), new byte[] { 0x4D, 0x5A });
    }
}

public class CardServerConfigTests
{
    [Theory]
    [InlineData("203.0.113.10:28080", "http://203.0.113.10:28080/match", "203.0.113.10")]
    [InlineData("http://mm.example:28080/match", "http://mm.example:28080/match", "mm.example")]
    [InlineData("https://mm.example/match", "https://mm.example:28080/match", "mm.example")]
    public void Normalizes_match_url(string input, string url, string host)
    {
        Assert.True(CardServerConfig.TryNormalizeMatchUrl(input, out var got, out var h, out var err), err);
        Assert.Equal(url, got);
        Assert.Equal(host, h);
    }

    [Theory]
    [InlineData("127.0.0.1:28080")]
    [InlineData("http://localhost/match")]
    [InlineData("")]
    public void Rejects_loopback_or_empty_match_url(string input)
    {
        Assert.False(CardServerConfig.TryNormalizeMatchUrl(input, out _, out _, out var err));
        Assert.False(string.IsNullOrWhiteSpace(err));
    }

    [Fact]
    public void Finds_server_json_and_never_returns_or_logs_tag_secret()
    {
        using var dir = new TempDir();
        var json = Path.Combine(dir.Root, "Configurations", "server.json");
        Directory.CreateDirectory(Path.GetDirectoryName(json)!);
        var secret = "super-secret-tag-value-XYZ";
        File.WriteAllText(json, """
            {
              "OnlineMatching": { "MatchUrl": "http://old:28080/match", "StunHost": "old" },
              "OnlineTag": { "TagSecret": "super-secret-tag-value-XYZ" }
            }
            """);

        var found = CardServerConfig.FindServerJson(Path.Combine(dir.Root, "3-Start-Server.bat"));
        Assert.Equal(json, found);
        var pub = CardServerConfig.ReadPublic(json)!;
        Assert.True(pub.HasTagSecret);
        Assert.Equal("http://old:28080/match", pub.MatchUrl);
        Assert.DoesNotContain(secret, pub.ToString());

        var report = CardServerConfig.Apply(json, "203.0.113.9:28080", "new-secret-value-123", new BackupService(() => new DateTime(2026, 11, 15, 12, 0, 0)));
        Assert.Contains(json, report.Written);
        Assert.Contains(report.Notes, n => n.Contains("MatchUrl") && n.Contains("http://203.0.113.9:28080/match"));
        Assert.Contains(report.Notes, n => n.Contains("TagSecret") && n.Contains(Secrets.Mask("new-secret-value-123")));
        foreach (var n in report.Notes.Concat(report.Written).Concat(report.Backups))
        {
            Assert.DoesNotContain("super-secret-tag-value-XYZ", n);
            Assert.DoesNotContain("new-secret-value-123", n);
        }
        var updated = File.ReadAllText(json);
        Assert.Contains("new-secret-value-123", updated);
        Assert.Contains("203.0.113.9", updated);
        Assert.DoesNotContain("super-secret-tag-value-XYZ", string.Join("\n", report.Notes));
        var again = CardServerConfig.ReadPublic(json)!;
        Assert.True(again.HasTagSecret);
        Assert.DoesNotContain("new-secret-value-123", again.ToString());
    }

    [Fact]
    public void Empty_secret_leaves_existing_secret_alone()
    {
        using var dir = new TempDir();
        var json = Path.Combine(dir.Root, "Configurations", "server.json");
        Directory.CreateDirectory(Path.GetDirectoryName(json)!);
        File.WriteAllText(json, """{"OnlineTag":{"TagSecret":"keep-me"}}""");
        var r = CardServerConfig.Apply(json, matchUrl: null, tagSecret: "  ", backups: new BackupService());
        Assert.Contains(json, r.Unchanged);
        Assert.Contains("keep-me", File.ReadAllText(json));
    }
}

public class WizardPlannerTests
{
    [Fact]
    public void Preview_and_apply_write_card_serial_ips_and_server_json_without_logging_secrets()
    {
        using var g = new TempGameFolder(withCard: false);
        GameFolderValidatorTests.TouchGameBits(g);
        var serverRoot = Path.Combine(g.Root, "mm");
        Directory.CreateDirectory(Path.Combine(serverRoot, "Configurations"));
        var json = Path.Combine(serverRoot, "Configurations", "server.json");
        File.WriteAllText(json, """{"OnlineMatching":{"MatchUrl":""},"OnlineTag":{"TagSecret":""}}""");
        File.WriteAllText(Path.Combine(serverRoot, "3-Start-Server.bat"), "rem");

        var ids = new IdentityGenerator(_ => 1);
        var card = ids.NewCard();
        var state = new WizardState
        {
            ProfileName = "Test cab",
            GameFolder = g.Root,
            GenerateNewCard = true,
            PendingCard = card,
            Serial = "Abcd",
            SerialAction = SerialAction.WroteBecauseMissing,
            OnlyCabOnThisPc = false,
            PcbIp = "192.168.1.69",
            LmIp = "192.168.1.245",
            CardServerPath = Path.Combine(serverRoot, "3-Start-Server.bat"),
            MatchUrl = "203.0.113.10:28080",
            TagSecret = "wizard-tag-secret-999",
        };

        var file = new ProfileFile();
        var writer = new GameConfigWriter(new BackupService(() => new DateTime(2026, 11, 15, 13, 0, 0)));
        var preview = WizardPlanner.Preview(state, file, writer);
        Assert.True(preview.CanApply, string.Join("; ", preview.Blocking));
        Assert.Contains(preview.Writes, w => w.Kind == "card.ini" && w.Summary.Contains("never share", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(preview.Writes, w => w.Kind == "lmnfig.ini" && w.Summary.Contains("192.168.1.245"));
        Assert.Contains(preview.Writes, w => w.Kind == "server.json" && w.Summary.Contains(Secrets.Mask("wizard-tag-secret-999")));
        Assert.DoesNotContain(preview.Writes, w => w.Summary.Contains("wizard-tag-secret-999") || w.Summary.Contains(card.AccessCode));
        Assert.DoesNotContain(state.ToString(), card.AccessCode);
        Assert.DoesNotContain(state.ToString(), "wizard-tag-secret-999");

        var report = WizardPlanner.Apply(state, file, writer);
        Assert.Single(file.Profiles);
        Assert.Equal("Test cab", file.Profiles[0].Name);
        Assert.Equal("Abcd", file.Profiles[0].Serial);
        Assert.Equal("192.168.1.245", file.Profiles[0].LmIp);
        Assert.False(file.Profiles[0].AutoDetectIp);
        Assert.Contains("accessCode=" + card.AccessCode, File.ReadAllText(g.P("card.ini")));
        Assert.Contains("serial =Abcd", File.ReadAllText(g.P("config.ini")).Replace("serial=Abcd", "serial =Abcd"));
        Assert.Contains("IpAddress=192.168.1.245", File.ReadAllText(g.P("lmnfig.ini")));
        Assert.Contains("wizard-tag-secret-999", File.ReadAllText(json));
        Assert.Contains("http://203.0.113.10:28080/match", File.ReadAllText(json));
        var dumped = string.Join("\n", report.Notes.Concat(report.Written));
        Assert.DoesNotContain("wizard-tag-secret-999", dumped);
        Assert.DoesNotContain(card.AccessCode, dumped);
        Assert.DoesNotContain(card.ChipId, dumped);
    }

    [Fact]
    public void Preview_blocks_loopback_lm_and_keeps_valid_existing_card()
    {
        using var g = new TempGameFolder();
        GameFolderValidatorTests.TouchGameBits(g);
        var state = new WizardState
        {
            ProfileName = "Main",
            GameFolder = g.Root,
            GenerateNewCard = false,
            Serial = "jZJj",
            SerialAction = SerialAction.AdoptedExisting,
            PcbIp = "192.168.1.69",
            LmIp = "127.0.0.1",
        };
        var preview = WizardPlanner.Preview(state, new ProfileFile(), new GameConfigWriter());
        Assert.False(preview.CanApply);
        Assert.Contains(preview.Blocking, b => b.Contains("21-01"));
        Assert.Contains(preview.Notes, n => n.Contains("Existing valid card.ini"));
        Assert.DoesNotContain(preview.Writes, w => w.Kind == "card.ini");
    }

    [Fact]
    public void ToString_masks_card_and_secret()
    {
        var s = new WizardState { PendingCard = new CardIdentity("11112222333344445555", "0000000000000000000000000000ABCD"), TagSecret = "abcd" };
        var t = s.ToString();
        Assert.DoesNotContain("11112222333344445555", t);
        Assert.DoesNotContain("0000000000000000000000000000ABCD", t);
        Assert.DoesNotContain("abcd", t.Split("TagSecret", 2).Last());
        Assert.Contains(Secrets.Mask("abcd"), t);
    }
}

public class WizardChecksTests
{
    [Fact]
    public void Lists_exact_exes_and_detects_TinyWall()
    {
        using var g = new TempGameFolder();
        GameFolderValidatorTests.TouchGameBits(g);
        var server = Path.Combine(g.Root, "mm", "Server-Infinite.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(server)!);
        File.WriteAllBytes(server, new byte[] { 0x4D, 0x5A });
        var exes = WizardChecks.ExesToAllow(g.Root, server);
        Assert.Contains(g.P(GameFolder.ClientExeName), exes);
        Assert.Contains(g.P(GameFolder.LmExeName), exes);
        Assert.Contains(server, exes);

        var probe = new FakeProbe();
        probe.Files.Add(Path.Combine("C:\\Program Files", "TinyWall", "TinyWall.exe"));
        probe.Roots.Add("C:\\Program Files");
        probe.Services.Add("mpssvc");
        var report = WizardChecks.DetectFirewall(probe, g.Root, server);
        Assert.True(report.TinyWallLikely);
        Assert.True(report.WindowsFirewallLikely);
        Assert.Contains(server, report.ExesToAllow);
        var advice = WizardChecks.FirewallAdvice(report);
        Assert.Contains("TinyWall", advice);
        Assert.Contains("will not change", advice, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(server, advice);
    }

    [Fact]
    public async Task Matchmaker_http_treats_any_status_as_reachable()
    {
        var handler = new StubHandler(new HttpResponseMessage(HttpStatusCode.NotFound));
        var ok = await WizardChecks.MatchmakerHttpAsync("http://mm.example:28080/match", handler);
        Assert.True(ok.Ok);
        Assert.Contains("404", ok.Message);
        Assert.DoesNotContain("user:pass", ok.Message);

        var fail = await WizardChecks.MatchmakerHttpAsync("127.0.0.1:28080", handler);
        Assert.False(fail.Ok);

        var boom = await WizardChecks.MatchmakerHttpAsync("http://mm.example:28080/match", new StubHandler(new HttpRequestException("connection refused")));
        Assert.False(boom.Ok);
        Assert.Contains("connection refused", boom.Message);
    }

    [Fact]
    public async Task Card_server_port_reuses_existing_patched_server_and_does_not_kill_it()
    {
        var state = new WizardState { CardServerPath = @"C:\mm\Server-Infinite.exe", CardServerPort = 28080 };
        var starter = new RecordingStarter();
        var owners = new FixedOwners(new PortOwner(4242, "Server-Infinite", @"C:\mm\Server-Infinite.exe"));
        var r = await WizardChecks.CardServerPortAsync(state, new FixedProbe(true), owners, starter);
        Assert.True(r.Ok);
        Assert.Contains("already answers", r.Message);
        Assert.Contains("left running", r.Message);
        Assert.Empty(starter.Started);
        Assert.Empty(starter.Killed);
    }

    [Fact]
    public async Task Card_server_port_test_starts_then_stops_only_what_it_started()
    {
        var state = new WizardState { CardServerPath = Path.GetTempFileName(), CardServerPort = 28090 };
        var starter = new RecordingStarter();
        var probe = new SequenceProbe(false, false, true);
        var r = await WizardChecks.CardServerPortAsync(state, probe, new FixedOwners(null), starter,
            delay: new InstantDelay(), timeout: TimeSpan.FromSeconds(2));
        Assert.True(r.Ok);
        Assert.Contains("Test-started", r.Message);
        Assert.Contains("stopped", r.Message);
        Assert.Single(starter.Started);
        Assert.Equal(starter.Started[0].Id, starter.Killed.Single());
        File.Delete(state.CardServerPath);
    }

    [Fact]
    public async Task Card_server_port_does_not_stop_unrelated_listener()
    {
        var state = new WizardState { CardServerPath = @"C:\mm\Server-Infinite.exe", CardServerPort = 28080 };
        var starter = new RecordingStarter();
        var owners = new FixedOwners(new PortOwner(7, "chrome", @"C:\chrome.exe"));
        var r = await WizardChecks.CardServerPortAsync(state, new FixedProbe(true), owners, starter);
        Assert.False(r.Ok);
        Assert.Contains("already in use", r.Message);
        Assert.Contains("not stopped", r.Message);
        Assert.Empty(starter.Killed);
    }

    [Fact]
    public void Troubleshooting_covers_expected_errors()
    {
        Assert.Contains("21-01", TroubleshootingTopics.LmDisconnect.Body);
        Assert.Contains("127.0.0.1", TroubleshootingTopics.LmDisconnect.Body);
        Assert.Contains("このカードは使用中です", TroubleshootingTopics.CardInUse.Title);
        Assert.Contains("Server-Infinite", TroubleshootingTopics.CardServer.Body);
        Assert.Contains("never changes firewall", TroubleshootingTopics.Firewall.Body, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(4, TroubleshootingTopics.All.Count);
    }
}

internal sealed class TempDir : IDisposable
{
    public string Root { get; } = Path.Combine(Path.GetTempPath(), "exvs2-launcher-test-" + Guid.NewGuid().ToString("N"));
    public TempDir() => Directory.CreateDirectory(Root);
    public void Dispose() { try { Directory.Delete(Root, true); } catch { } }
}

internal sealed class FakeProbe : IEnvironmentProbe
{
    public List<string> Files { get; } = new();
    public List<string> Processes { get; } = new();
    public List<string> Services { get; } = new();
    public List<string> Roots { get; } = new();
    public bool FileExists(string path) => Files.Any(f => string.Equals(f, path, StringComparison.OrdinalIgnoreCase));
    public bool ProcessNameRunning(string name) => Processes.Any(p => string.Equals(p, name, StringComparison.OrdinalIgnoreCase));
    public bool ServiceRunning(string serviceName) => Services.Any(s => string.Equals(s, serviceName, StringComparison.OrdinalIgnoreCase));
    public IReadOnlyList<string> ProgramFilesRoots() => Roots;
}

internal sealed class StubHandler : HttpMessageHandler
{
    private readonly object _result;
    public StubHandler(object result) => _result = result;
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (_result is Exception ex) return Task.FromException<HttpResponseMessage>(ex);
        return Task.FromResult((HttpResponseMessage)_result);
    }
}

internal sealed class FixedProbe : IPortProbe
{
    private readonly bool _listening;
    public FixedProbe(bool listening) => _listening = listening;
    public Task<bool> IsListeningAsync(string host, int port, CancellationToken ct) => Task.FromResult(_listening);
}

internal sealed class SequenceProbe : IPortProbe
{
    private readonly Queue<bool> _answers;
    public SequenceProbe(params bool[] answers) => _answers = new Queue<bool>(answers);
    public Task<bool> IsListeningAsync(string host, int port, CancellationToken ct) =>
        Task.FromResult(_answers.Count > 0 ? _answers.Dequeue() : true);
}

internal sealed class FixedOwners : IPortOwnerLookup
{
    private readonly PortOwner? _owner;
    public FixedOwners(PortOwner? owner) => _owner = owner;
    public PortOwner? FindListener(int port) => _owner;
}

internal sealed class InstantDelay : IDelay
{
    public Task WaitAsync(TimeSpan duration, CancellationToken ct) => Task.CompletedTask;
}

internal sealed class RecordingStarter : IProcessStarter
{
    public List<FakeProc> Started { get; } = new();
    public List<int> Killed { get; } = new();
    public IStartedProcess Start(LaunchSpec spec)
    {
        var p = new FakeProc(spec.Role, 9000 + Started.Count, Killed);
        Started.Add(p);
        return p;
    }
}

internal sealed class FakeProc : IStartedProcess
{
    private readonly List<int> _killed;
    public FakeProc(string role, int id, List<int> killed) { Role = role; Id = id; _killed = killed; }
    public string Role { get; }
    public int Id { get; }
    public bool HasExited { get; private set; }
    public int? ExitCode { get; private set; }
    public void Kill() { HasExited = true; ExitCode = -1; _killed.Add(Id); }
}

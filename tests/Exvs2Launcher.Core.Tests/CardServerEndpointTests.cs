using Exvs2Launcher.Core.Files;
using Exvs2Launcher.Core.Net;
using Exvs2Launcher.Core.Profiles;
using Exvs2Launcher.Core.Wizard;

namespace Exvs2Launcher.Core.Tests;

public class CardServerEndpointTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("192.168.1.245")]
    [InlineData("192.168.1.245:")]
    [InlineData("127.0.0.1:0")]
    [InlineData("127.0.0.1:99999")]
    [InlineData("host")]
    public void TryParse_rejects_missing_or_invalid_port(string? value)
    {
        Assert.False(CardServerEndpoint.TryParse(value, out _, out _));
    }

    [Fact]
    public void TryParse_reads_host_and_port()
    {
        Assert.True(CardServerEndpoint.TryParse("127.0.0.1:28080", out var host, out var port));
        Assert.Equal("127.0.0.1", host);
        Assert.Equal(28080, port);
    }

    [Fact]
    public void Problem_for_bare_lm_ip_mentions_port_and_21_32()
    {
        var reason = CardServerEndpoint.Problem("192.168.1.245", "lmnfig.ini", "127.0.0.1:28080");
        Assert.NotNull(reason);
        Assert.Contains("lmnfig.ini", reason);
        Assert.Contains("192.168.1.245", reason);
        Assert.Contains("port", reason, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("21-32", reason);
        Assert.Contains("127.0.0.1:28080", reason);
    }

    [Fact]
    public void Problem_for_mismatch_mentions_expected_address()
    {
        var reason = CardServerEndpoint.Problem("10.10.0.2:28080", "config.ini", "127.0.0.1:28080");
        Assert.NotNull(reason);
        Assert.Contains("does not match", reason);
        Assert.Contains("21-32", reason);
    }

    [Theory]
    [InlineData("127.0.0.1:28080")]
    [InlineData("localhost:28080")]
    [InlineData(" 127.0.0.1:28080 ")]
    public void Matching_loopback_is_accepted(string value)
    {
        Assert.Null(CardServerEndpoint.Problem(value, "lmnfig.ini", "127.0.0.1:28080"));
        Assert.True(CardServerEndpoint.Same(value, "127.0.0.1:28080"));
    }

    [Fact]
    public void Inspect_reports_both_files()
    {
        var issues = CardServerEndpoint.Inspect("192.168.1.245", "192.168.1.245", "127.0.0.1:28080");
        Assert.Equal(2, issues.Count);
        Assert.Contains(issues, i => i.Contains("config.ini"));
        Assert.Contains(issues, i => i.Contains("lmnfig.ini"));
    }

    [Fact]
    public void Inspect_is_empty_when_both_match()
    {
        Assert.Empty(CardServerEndpoint.Inspect("127.0.0.1:28080", "127.0.0.1:28080", "127.0.0.1:28080"));
    }

    [Fact]
    public void Wizard_preview_and_apply_fix_a_bare_lm_server_address()
    {
        using var g = new TempGameFolder();
        GameFolderValidatorTests.TouchGameBits(g);
        File.WriteAllText(g.P("lmnfig.ini"),
            "[config]\r\nWindowed=True\r\nServer=192.168.1.245\r\nMode=LM\r\nIpAddress=192.168.1.245\r\n");

        var state = new WizardState
        {
            ProfileName = "Main",
            GameFolder = g.Root,
            GenerateNewCard = false,
            Serial = "jZJj",
            SerialAction = SerialAction.AdoptedExisting,
            PcbIp = "192.168.1.69",
            LmIp = "192.168.1.245",
            CardServerPort = 28080,
        };
        var writer = new GameConfigWriter(new BackupService(() => new DateTime(2026, 11, 15, 14, 0, 0)));
        var preview = WizardPlanner.Preview(state, new ProfileFile(), writer);
        Assert.True(preview.CanApply, string.Join("; ", preview.Blocking));
        Assert.Contains(preview.Notes, n => n.Contains("lmnfig.ini") && n.Contains("21-32"));
        Assert.Contains(preview.Writes, w => w.Kind == "lmnfig.ini" && w.Summary.Contains("192.168.1.245") && w.Summary.Contains("127.0.0.1:28080") && w.Summary.Contains("21-32"));

        var report = WizardPlanner.Apply(state, new ProfileFile(), writer);
        Assert.Contains("Server=127.0.0.1:28080", File.ReadAllText(g.P("lmnfig.ini")));
        Assert.Contains("IpAddress=192.168.1.245", File.ReadAllText(g.P("lmnfig.ini")));
        Assert.Contains("Server =127.0.0.1:28080", File.ReadAllText(g.P("config.ini")));
        Assert.Contains(report.Notes, n => n.Contains("lmnfig.ini Server:") && n.Contains("192.168.1.245") && n.Contains("127.0.0.1:28080"));
        Assert.NotEmpty(g.Backups("lmnfig.ini"));
    }
}

using Exvs2Launcher.Core.Files;
using Exvs2Launcher.Core.Net;
using Exvs2Launcher.Core.Profiles;
using Exvs2Launcher.Core.Wizard;

namespace Exvs2Launcher.Core.Tests;

public class SocketHookSettingTests
{
    [Theory]
    [InlineData("True")]
    [InlineData("true")]
    [InlineData("1")]
    [InlineData("yes")]
    public void IsDisabled_accepts_trueish_values(string value) =>
        Assert.True(SocketHookSetting.IsDisabled(value));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("False")]
    [InlineData("false")]
    [InlineData("0")]
    public void IsDisabled_is_false_when_hook_is_on_or_unset(string? value) =>
        Assert.False(SocketHookSetting.IsDisabled(value));

    [Fact]
    public void Warns_when_hook_is_off_and_lm_ip_differs_from_pcb()
    {
        var warn = SocketHookSetting.WarnIfDangerous("True", "192.168.1.69", "192.168.1.245", multipleCabs: false);
        Assert.NotNull(warn);
        Assert.Contains("DisableSocketHook=True", warn);
        Assert.Contains("the LM will grab every address and other LMs fail with 21-32", warn);
    }

    [Fact]
    public void Warns_when_hook_is_off_and_multiple_cabs_exist()
    {
        var warn = SocketHookSetting.WarnIfDangerous("True", "192.168.1.69", "192.168.1.69", multipleCabs: true);
        Assert.NotNull(warn);
        Assert.Contains("21-32", warn);
    }

    [Fact]
    public void No_warning_when_hook_is_off_on_a_single_cab_sharing_one_ip()
    {
        Assert.Null(SocketHookSetting.WarnIfDangerous("True", "192.168.1.69", "192.168.1.69", multipleCabs: false));
    }

    [Fact]
    public void No_warning_when_hook_is_on()
    {
        Assert.Null(SocketHookSetting.WarnIfDangerous("False", "192.168.1.69", "192.168.1.245", multipleCabs: true));
    }

    [Fact]
    public void Warns_when_snowlauncher_ini_would_turn_the_hook_off()
    {
        var warn = SocketHookSetting.WarnIfDangerous("False", "192.168.1.69", "192.168.1.245", true, snowHook: "True");
        Assert.NotNull(warn);
        Assert.Contains("SnowLauncher.ini", warn);
        Assert.Contains("was not written to lmnfig.ini", warn);
        Assert.Contains("21-32", warn);
    }

    [Fact]
    public void Apply_sets_false_and_never_writes_true()
    {
        using var g = new TempGameFolder();
        File.WriteAllText(g.P("lmnfig.ini"),
            "[config]\r\nWindowed=True\r\nServer=127.0.0.1:28080\r\nMode=LM\r\nIpAddress=192.168.1.245\r\nDisableSocketHook=True\r\n");
        File.WriteAllText(g.P("SnowLauncher.ini"),
            TempGameFolder.Snow + "DisableSocketHook=True\r\n");

        var w = new GameConfigWriter(new BackupService(() => new DateTime(2026, 11, 15, 15, 0, 0)));
        var r = w.ApplyLmSocketHookEnabled(g.Root, syncSnowLauncher: true);
        Assert.Contains("DisableSocketHook=False", g.Text("lmnfig.ini"));
        Assert.DoesNotContain("DisableSocketHook=True", g.Text("lmnfig.ini"));
        Assert.Contains("DisableSocketHook=False", g.Text("SnowLauncher.ini"));
        Assert.Contains(r.Notes, n => n.Contains("lmnfig.ini DisableSocketHook:") && n.Contains("True") && n.Contains("False"));
        Assert.NotEmpty(g.Backups("lmnfig.ini"));
    }

    [Fact]
    public void Network_sync_does_not_push_snow_true_into_lmnfig()
    {
        using var g = new TempGameFolder();
        File.WriteAllText(g.P("lmnfig.ini"),
            "[config]\r\nWindowed=True\r\nServer=127.0.0.1:28080\r\nMode=LM\r\nIpAddress=192.168.1.245\r\nDisableSocketHook=False\r\n");
        File.WriteAllText(g.P("SnowLauncher.ini"),
            TempGameFolder.Snow + "DisableSocketHook=True\r\n");

        var w = new GameConfigWriter(new BackupService(() => new DateTime(2026, 11, 15, 15, 0, 0)));
        var r = w.ApplyNetwork(g.Root, "192.168.1.69", "192.168.1.245", "127.0.0.1:28080", true);
        Assert.Contains("DisableSocketHook=False", g.Text("lmnfig.ini"));
        Assert.DoesNotContain("DisableSocketHook=True", g.Text("lmnfig.ini"));
        Assert.Contains("DisableSocketHook=True", g.Text("SnowLauncher.ini")); // left alone unless user confirms
        Assert.Contains(r.Notes, n => n.Contains("SnowLauncher.ini has DisableSocketHook=True") && n.Contains("was not written to lmnfig.ini"));
        var state = w.Read(g.Root);
        Assert.Equal("False", state.LmDisableSocketHook);
        Assert.Equal("True", state.SnowDisableSocketHook);
    }

    [Fact]
    public void Wizard_preview_and_apply_offer_to_set_hook_false_when_ips_differ()
    {
        using var g = new TempGameFolder();
        GameFolderValidatorTests.TouchGameBits(g);
        File.WriteAllText(g.P("lmnfig.ini"),
            "[config]\r\nWindowed=True\r\nServer=127.0.0.1:28080\r\nMode=LM\r\nIpAddress=192.168.1.245\r\nDisableSocketHook=True\r\n");

        var state = new WizardState
        {
            ProfileName = "Main",
            GameFolder = g.Root,
            GenerateNewCard = false,
            Serial = "jZJj",
            SerialAction = SerialAction.AdoptedExisting,
            OnlyCabOnThisPc = false,
            PcbIp = "192.168.1.69",
            LmIp = "192.168.1.245",
        };
        var writer = new GameConfigWriter(new BackupService(() => new DateTime(2026, 11, 15, 15, 0, 0)));
        var preview = WizardPlanner.Preview(state, new ProfileFile(), writer);
        Assert.True(preview.CanApply, string.Join("; ", preview.Blocking));
        Assert.Contains(preview.Notes, n => n.Contains("DisableSocketHook=True") && n.Contains("21-32"));
        Assert.Contains(preview.Writes, w => w.Kind == "lmnfig.ini" && w.Summary.Contains("DisableSocketHook=True → False"));

        WizardPlanner.Apply(state, new ProfileFile(), writer);
        Assert.Contains("DisableSocketHook=False", File.ReadAllText(g.P("lmnfig.ini")));
        Assert.Contains("IpAddress=192.168.1.245", File.ReadAllText(g.P("lmnfig.ini")));
    }

    [Fact]
    public void Wizard_does_not_silently_flip_hook_on_a_safe_single_cab()
    {
        using var g = new TempGameFolder();
        GameFolderValidatorTests.TouchGameBits(g);
        File.WriteAllText(g.P("lmnfig.ini"),
            "[config]\r\nWindowed=True\r\nServer=127.0.0.1:28080\r\nMode=LM\r\nIpAddress=192.168.1.69\r\nDisableSocketHook=True\r\n");

        var state = new WizardState
        {
            ProfileName = "Solo",
            GameFolder = g.Root,
            GenerateNewCard = false,
            Serial = "jZJj",
            SerialAction = SerialAction.AdoptedExisting,
            OnlyCabOnThisPc = true,
            PcbIp = "192.168.1.69",
            LmIp = "192.168.1.69",
        };
        var preview = WizardPlanner.Preview(state, new ProfileFile(), new GameConfigWriter());
        Assert.True(preview.CanApply, string.Join("; ", preview.Blocking));
        Assert.Contains(preview.Notes, n => n.Contains("DisableSocketHook=True"));
        Assert.DoesNotContain(preview.Writes, w => w.Summary.Contains("DisableSocketHook"));
    }
}

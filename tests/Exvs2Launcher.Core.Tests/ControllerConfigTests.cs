using Exvs2Launcher.Core.Files;
using Exvs2Launcher.Core.Input;
using Exvs2Launcher.Core.Ini;

namespace Exvs2Launcher.Core.Tests;

public class ControllerComboTests
{
    [Fact]
    public void Expand_keeps_observed_combo_order()
    {
        var slot = new ControllerSlot
        {
            ButtonA = "1,5",
            ButtonB = "4",
            ButtonC = "2",
            ButtonD = "3",
            ButtonAB = "6",
            ButtonBC = "8",
            ButtonAC = "7",
            ButtonABC = "12",
        };
        Assert.Equal("1,5,6,7,12", ControllerCombos.ExpandA(slot));
        Assert.Equal("4,6,8,12", ControllerCombos.ExpandB(slot));
        Assert.Equal("2,7,8,12", ControllerCombos.ExpandC(slot));
        Assert.Equal("3", ControllerCombos.ExpandD(slot));
    }

    [Fact]
    public void Infers_combos_from_expanded_lists_and_explicit_primaries()
    {
        var slot = new ControllerSlot();
        ControllerCombos.FillFromExpanded(
            slot,
            expandedA: "1,5,6,7,12",
            expandedB: "4,6,8,12",
            expandedC: "2,7,8,12",
            expandedD: "3",
            explicitA: "1",
            explicitB: "4",
            explicitC: "2",
            explicitD: "3");

        Assert.Equal("1,5", slot.ButtonA);
        Assert.Equal("4", slot.ButtonB);
        Assert.Equal("2", slot.ButtonC);
        Assert.Equal("3", slot.ButtonD);
        Assert.Equal("6", slot.ButtonAB);
        Assert.Equal("8", slot.ButtonBC);
        Assert.Equal("7", slot.ButtonAC);
        Assert.Equal("12", slot.ButtonABC);
        Assert.Equal("1,5,6,7,12", ControllerCombos.ExpandA(slot));
    }

    [Fact]
    public void Infers_combos_without_explicit_primary_keys()
    {
        var slot = new ControllerSlot();
        ControllerCombos.FillFromExpanded(slot, "1,5,6,7,12", "4,6,8,12", "2,7,8,12", "3");
        Assert.Equal("1,5", slot.ButtonA);
        Assert.Equal("4", slot.ButtonB);
        Assert.Equal("2", slot.ButtonC);
        Assert.Equal("3", slot.ButtonD);
        Assert.Equal("6", slot.ButtonAB);
        Assert.Equal("8", slot.ButtonBC);
        Assert.Equal("7", slot.ButtonAC);
        Assert.Equal("12", slot.ButtonABC);
        Assert.Equal("1,5,6,7,12", ControllerCombos.ExpandA(slot));
    }

    [Fact]
    public void ParseList_rejects_non_numeric_and_zero()
    {
        Assert.False(ControllerCombos.TryParseList("1,foo", out _, out var err));
        Assert.Contains("foo", err);
        Assert.False(ControllerCombos.TryParseList("0,1", out _, out _));
        Assert.True(ControllerCombos.TryParseList("1, 2, 2, 3", out var ids, out _));
        Assert.Equal(new[] { 1, 2, 3 }, ids);
        Assert.True(ControllerCombos.TryParseDeviceId("16", out var id, out _));
        Assert.Equal(16, id);
        Assert.False(ControllerCombos.TryParseDeviceId("17", out _, out _));
        Assert.True(ControllerCombos.TryParseDeviceId("", out var empty, out _));
        Assert.Null(empty);
    }
}

public class ControllerConfigWriterTests
{
    private const string Config =
        "[config]\r\nmode =Client\r\nserial =AbCd\r\nServer =127.0.0.1:28080\r\nIpAddress=192.168.1.10\r\n\r\n" +
        "[keyboard]\r\nEnabled=True\r\nA=J,U,Y,T\r\n\r\n" +
        "[controller]\r\nEnabled=False\r\nArcadeButton1=4\r\nArcadeButton2=8\r\nArcadeStartButton=16\r\n" +
        "DeviceId=15\r\nButtonA=1\r\nButtonB=4\r\nButtonC=2\r\nButtonD=3\r\nName=Razer Panthera PS4\r\n" +
        "A=1,5,6,7,12\r\nB=4,6,8,12\r\nC=2,7,8,12\r\nD=3\r\nStart=10,11\r\nCoin=14\r\nCard=14\r\n\r\n" +
        "[display]\r\nResolution=480p\r\n\r\n" +
        "[controller-1]\r\nEnabled=False\r\nName=Controller (Xbox One For Windows)\r\nMode=xinput-native\r\n\r\n" +
        "[xinput]\r\nEmulatedXInputEnabled=True\r\n";

    private const string Snow =
        "[AppSettings]\r\nStartClient=True\r\n[Game_Config]\r\nSerial=AbCd\r\n" +
        "[Game_Controller]\r\nEnabled=False\r\nButtonA=1,5\r\nButtonB=4\r\nButtonC=2\r\nButtonD=3\r\n" +
        "ButtonAB=6\r\nButtonBC=8\r\nButtonAC=7\r\nButtonABC=12\r\nCoin=14\r\nCard=14\r\nStart=10,11\r\n" +
        "Test=9\r\nService=15\r\nKill=13\r\nName=Razer Panthera PS4\r\nKillBool=False\r\nTestBool=False\r\nServiceBool=False\r\n" +
        "[Game_Controller_1]\r\nEnabled=False\r\nName=Controller (Xbox One For Windows)\r\n";

    private static ControllerConfigWriter Writer() =>
        new(new BackupService(() => new DateTime(2026, 11, 15, 15, 0, 0)));

    private sealed class TempCtrl : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "exvs2-launcher-ctrl-" + Guid.NewGuid().ToString("N"));
        public TempCtrl(string config = Config, string? snow = Snow)
        {
            Directory.CreateDirectory(Root);
            File.WriteAllText(Path.Combine(Root, "config.ini"), config);
            if (snow is not null) File.WriteAllText(Path.Combine(Root, "SnowLauncher.ini"), snow);
        }
        public string Text(string name) => File.ReadAllText(Path.Combine(Root, name));
        public void Dispose() { try { Directory.Delete(Root, true); } catch { } }
    }

    [Fact]
    public void Reads_observed_panthera_bindings_and_xinput_section()
    {
        using var g = new TempCtrl();
        var state = Writer().Read(g.Root);
        Assert.Equal("Razer Panthera PS4", state.Controller.Name);
        Assert.False(state.Controller.Enabled);
        Assert.Equal(15, state.Controller.DeviceId);
        // SnowLauncher ButtonA=1,5 is used as the explicit primary when present.
        Assert.Equal("1,5", state.Controller.ButtonA);
        Assert.Equal("4", state.Controller.ButtonB);
        Assert.Equal("6", state.Controller.ButtonAB);
        Assert.Equal("7", state.Controller.ButtonAC);
        Assert.Equal("8", state.Controller.ButtonBC);
        Assert.Equal("12", state.Controller.ButtonABC);
        Assert.Equal("10,11", state.Controller.Start);
        Assert.Equal("9", state.Controller.Test);
        Assert.Equal("Controller (Xbox One For Windows)", state.Controller1.Name);
        Assert.Equal("xinput-native", state.Controller1.Mode);
        Assert.True(state.XInput.EmulatedXInputEnabled);
        Assert.Null(state.XInput.Mode);
    }

    [Fact]
    public void Reads_from_config_ini_alone_and_recovers_combos()
    {
        using var g = new TempCtrl(snow: null);
        var state = Writer().Read(g.Root);
        Assert.Equal("Razer Panthera PS4", state.Controller.Name);
        Assert.Equal("1,5", state.Controller.ButtonA);
        Assert.Equal("6", state.Controller.ButtonAB);
        Assert.Equal("12", state.Controller.ButtonABC);
        Assert.Null(state.Controller.Test);
    }

    [Fact]
    public void Write_enables_stick_preserves_arcade_keys_and_backups()
    {
        using var g = new TempCtrl();
        var state = Writer().Read(g.Root);
        state.Controller.Enabled = true;
        state.Controller.ButtonA = "1,5";
        var r = Writer().Apply(g.Root, state, syncSnowLauncher: true);

        var cfg = g.Text("config.ini");
        Assert.Contains("Enabled=True", cfg);
        Assert.Contains("ArcadeButton1=4", cfg);
        Assert.Contains("ArcadeStartButton=16", cfg);
        Assert.Contains("A=1,5,6,7,12", cfg);
        Assert.Contains("B=4,6,8,12", cfg);
        Assert.Contains("Name=Razer Panthera PS4", cfg);
        Assert.Contains("DeviceId=15", cfg);
        Assert.Contains("[keyboard]\r\nEnabled=True\r\nA=J,U,Y,T\r\n", cfg);
        Assert.Contains("Resolution=480p", cfg);
        Assert.Contains("EmulatedXInputEnabled=True", cfg);
        Assert.Contains("Mode=xinput-native", cfg);

        var snow = g.Text("SnowLauncher.ini");
        Assert.Contains("Enabled=True", snow);
        Assert.Contains("ButtonA=1,5", snow);
        Assert.Contains("ButtonAB=6", snow);
        Assert.Contains("KillBool=False", snow);

        Assert.Contains(r.Written, p => p.EndsWith("config.ini"));
        Assert.Contains(r.Written, p => p.EndsWith("SnowLauncher.ini"));
        Assert.True(File.Exists(Path.Combine(g.Root, "config.ini.bak-20261115-150000")));
        Assert.Equal(Config, File.ReadAllText(Path.Combine(g.Root, "config.ini.bak-20261115-150000")));
    }

    [Fact]
    public void Config_only_roundtrip_is_stable_and_silent_on_second_write()
    {
        using var g = new TempCtrl(snow: null);
        var first = Writer().Read(g.Root);
        Assert.Equal("1,5", first.Controller.ButtonA);
        var r1 = Writer().Apply(g.Root, first, syncSnowLauncher: false);
        Assert.NotEmpty(r1.Written);
        var mid = g.Text("config.ini");
        Assert.Contains("A=1,5,6,7,12", mid);
        Assert.Contains("ButtonA=1,5", mid);
        Assert.Contains("ArcadeButton1=4", mid);

        var second = Writer().Read(g.Root);
        var r2 = Writer().Apply(g.Root, second, syncSnowLauncher: false);
        Assert.Empty(r2.Written);
        Assert.Empty(r2.Backups);
        Assert.Equal(mid, g.Text("config.ini"));
        Assert.Single(Directory.GetFiles(g.Root, "config.ini.bak-*"));
    }

    [Fact]
    public void Writes_xinput_mode_and_device_id_and_leaves_unused_slot_uncreated()
    {
        const string slim =
            "[config]\r\nServer=127.0.0.1:28080\r\n[controller]\r\nEnabled=True\r\nName=Stick\r\nA=1\r\n[xinput]\r\nEmulatedXInputEnabled=False\r\n";
        using var g = new TempCtrl(slim, snow: null);
        var state = Writer().Read(g.Root);
        state.XInput.EmulatedXInputEnabled = true;
        state.XInput.Mode = "xinput-native";
        state.XInput.DeviceId = 16;
        var r = Writer().Apply(g.Root, state, false);
        var cfg = g.Text("config.ini");
        Assert.Contains("EmulatedXInputEnabled=True", cfg);
        Assert.Contains("Mode=xinput-native", cfg);
        Assert.Contains("DeviceId=16", cfg);
        Assert.DoesNotContain("[controller-1]", cfg);
        Assert.NotEmpty(r.Written);
    }

    [Fact]
    public void Rejects_invalid_button_list()
    {
        using var g = new TempCtrl(snow: null);
        var state = Writer().Read(g.Root);
        state.Controller.ButtonA = "A-button";
        var ex = Assert.Throws<ArgumentException>(() => Writer().Apply(g.Root, state, false));
        Assert.Contains("A-button", ex.Message);
        Assert.Equal(Config, g.Text("config.ini"));
        Assert.Empty(Directory.GetFiles(g.Root, "config.ini.bak-*"));
    }

    [Fact]
    public void Byte_identical_roundtrip_when_only_comments_and_unrelated_keys_exist()
    {
        const string text = "[controller]\r\n# keep me\r\nEnabled =False\r\n\r\nArcadeButton1 = 4\r\nName=Test Stick\r\n";
        using var g = new TempCtrl(text, snow: null);
        var state = Writer().Read(g.Root);
        // No binding keys present; writing an unused-looking slot still updates Enabled/Name (same values).
        var r = Writer().Apply(g.Root, state, false);
        Assert.Empty(r.Written);
        Assert.Equal(text, g.Text("config.ini"));
        var bytes = File.ReadAllBytes(Path.Combine(g.Root, "config.ini"));
        Assert.Equal(bytes, IniDocument.FromBytes(bytes).ToBytes());
    }
}

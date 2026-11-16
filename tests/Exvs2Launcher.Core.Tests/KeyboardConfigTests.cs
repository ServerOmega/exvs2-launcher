using Exvs2Launcher.Core.Files;
using Exvs2Launcher.Core.Input;
using Exvs2Launcher.Core.Ini;

namespace Exvs2Launcher.Core.Tests;

public class KeyboardKeyNameTests
{
    [Fact]
    public void Maps_virtual_keys_to_hook_names()
    {
        Assert.True(KeyboardKeyNames.TryGetName(0x0d, out var enter));
        Assert.Equal("Return", enter);
        Assert.True(KeyboardKeyNames.TryGetName(0x26, out var up));
        Assert.Equal("UpArr", up);
        Assert.True(KeyboardKeyNames.TryGetName(0x61, out var num1));
        Assert.Equal("1 (NumPad)", num1);
        Assert.False(KeyboardKeyNames.TryGetName(0x10, out _)); // Shift is not in the hook table
        Assert.False(KeyboardKeyNames.TryGetName(0xba, out _)); // OEM semicolon is not in the hook table
    }

    [Fact]
    public void Enter_is_an_alias_of_Return()
    {
        Assert.Equal("Return", KeyboardKeyNames.CanonicalName("Enter"));
        Assert.Equal("Return", KeyboardKeyNames.CanonicalName("enter"));
        Assert.Equal("Return", KeyboardKeyNames.CanonicalName("Return"));
        Assert.True(KeyboardKeyNames.TryGetVirtualKey("Enter", out int vk));
        Assert.Equal(0x0d, vk);
        Assert.Equal("Return", KeyboardKeyNames.FormatList(new[] { "enter" }));
    }

    [Fact]
    public void ParseList_trims_commas_and_canonicalizes()
    {
        Assert.Equal(new[] { "J", "U", "Y", "T" }, KeyboardKeyNames.ParseList("J, U, Y, T"));
        Assert.Equal("J,U,Y,T", KeyboardKeyNames.FormatList(KeyboardKeyNames.ParseList("j,u,y,t")));
        Assert.True(KeyboardKeyNames.TryParseList("Return,F3", out var names, out var err));
        Assert.Null(err);
        Assert.Equal(new[] { "Return", "F3" }, names);
        Assert.False(KeyboardKeyNames.TryParseList("LShift", out _, out var bad));
        Assert.Contains("LShift", bad);
    }
}

public class KeyboardComboTests
{
    [Fact]
    public void Expand_matches_observed_client_pack_lists()
    {
        var map = KeyboardDefaults.Client();
        Assert.Equal("J,U,Y,T", KeyboardCombos.ExpandA(map));
        Assert.Equal("I,U,O,T", KeyboardCombos.ExpandB(map));
        Assert.Equal("K,O,Y,T", KeyboardCombos.ExpandC(map));
        Assert.Equal("L", KeyboardCombos.ExpandD(map));
    }

    [Fact]
    public void Infers_combos_from_expanded_lists()
    {
        var map = new KeyboardMap();
        KeyboardCombos.FillFromExpanded(map, "J,U,Y,T", "I,U,O,T", "K,O,Y,T", "L");
        Assert.Equal("J", map.ButtonA);
        Assert.Equal("I", map.ButtonB);
        Assert.Equal("K", map.ButtonC);
        Assert.Equal("L", map.ButtonD);
        Assert.Equal("U", map.ButtonAB);
        Assert.Equal("O", map.ButtonBC);
        Assert.Equal("Y", map.ButtonAC);
        Assert.Equal("T", map.ButtonABC);
        Assert.Equal("J,U,Y,T", KeyboardCombos.ExpandA(map));
    }

    [Fact]
    public void Uses_explicit_snow_primaries_and_combos()
    {
        var map = new KeyboardMap();
        KeyboardCombos.FillFromExpanded(
            map,
            expandedA: "J,U,Y,T",
            expandedB: "I,U,O,T",
            expandedC: "K,O,Y,T",
            expandedD: "L",
            explicitA: "J",
            explicitB: "I",
            explicitC: "K",
            explicitD: "L",
            explicitAB: "U",
            explicitBC: "O",
            explicitAC: "Y",
            explicitABC: "T");
        Assert.Equal("J", map.ButtonA);
        Assert.Equal("U", map.ButtonAB);
        Assert.Equal("J,U,Y,T", KeyboardCombos.ExpandA(map));
    }

    [Fact]
    public void ProblemWith_requires_known_names_on_required_actions()
    {
        var map = KeyboardDefaults.Client();
        Assert.Null(KeyboardCombos.ProblemWith(map, "Client"));
        map.Up = "";
        Assert.Contains("Up needs a key", KeyboardCombos.ProblemWith(map, "Client"));
        map.Up = "LShift";
        Assert.Contains("LShift", KeyboardCombos.ProblemWith(map, "Client"));
        map = KeyboardDefaults.Lm();
        map.Service = null;
        Assert.Null(KeyboardCombos.ProblemWith(map, "LM"));
    }
}

public class KeyboardConfigWriterTests
{
    private const string Config =
        "[config]\r\nmode =Client\r\nserial =AbCd\r\nServer =127.0.0.1:28080\r\nIpAddress=192.168.1.10\r\n\r\n" +
        "[keyboard]\r\nEnabled =True\r\n\r\n" +
        "Up =W\r\nLeft =A\r\nDown =S\r\nRight =D\r\n" +
        "A =J,U,Y,T\r\nB =I,U,O,T\r\nC =K,O,Y,T\r\nD =L\r\n\r\n" +
        "Start =Return\r\nCoin =5\r\nCard =4\r\n\r\n" +
        "Service =F2\r\n\r\n" +
        "Kill =F4\r\nTest=F3\r\n" +
        "ButtonA=1 (NumPad)\r\nButtonR1=E\r\nButtonL2=4 (NumPad)\r\n\r\n" +
        "[controller]\r\nEnabled=False\r\nArcadeButton1=4\r\n\r\n" +
        "[display]\r\nResolution=480p\r\n";

    private const string Lm =
        "[config]\r\nMode=LM\r\nServer=127.0.0.1:28080\r\nIpAddress=192.168.1.10\r\n\r\n" +
        "[keyboard]\r\nEnabled=True\r\nUp=UpArr\r\nDown=DownArr\r\nLeft=LeftArr\r\nRight=RightArr\r\n" +
        "A=Z\r\nB=X\r\nC=C\r\nD=V\r\nCoin=5\r\nCard=P\r\nKill=F4\r\nStart=Q\r\nTest=F3\r\n\r\n" +
        "[display]\r\nResolution=144p\r\n";

    private const string Snow =
        "[AppSettings]\r\nStartClient=True\r\n[Game_Config]\r\nSerial=AbCd\r\n" +
        "[Game_Client_Keyboard]\r\nEnabled=True\r\nUp=W\r\nDown=S\r\nLeft=A\r\nRight=D\r\n" +
        "ButtonA=J\r\nButtonB=I\r\nButtonC=K\r\nButtonD=L\r\n" +
        "ButtonAB=U\r\nButtonBC=O\r\nButtonAC=Y\r\nButtonABC=T\r\n" +
        "Coin=5\r\nCard=4\r\nKill=F4\r\nStart=Return\r\nTest=F3\r\nService=F2\r\n" +
        "[Game_LM_Keyboard]\r\nEnabled=True\r\nUp=UpArr\r\nDown=DownArr\r\nLeft=LeftArr\r\nRight=RightArr\r\n" +
        "ButtonA=Z\r\nButtonB=X\r\nButtonC=C\r\nButtonD=V\r\n" +
        "Coin=5\r\nCard=P\r\nKill=F4\r\nStart=Q\r\nTest=F3\r\n";

    private static KeyboardConfigWriter Writer() =>
        new(new BackupService(() => new DateTime(2026, 11, 15, 16, 0, 0)));

    private sealed class TempKeys : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "exvs2-launcher-keys-" + Guid.NewGuid().ToString("N"));
        public TempKeys(string config = Config, string? lm = Lm, string? snow = Snow)
        {
            Directory.CreateDirectory(Root);
            File.WriteAllText(Path.Combine(Root, "config.ini"), config);
            if (lm is not null) File.WriteAllText(Path.Combine(Root, "lmnfig.ini"), lm);
            if (snow is not null) File.WriteAllText(Path.Combine(Root, "SnowLauncher.ini"), snow);
        }
        public string Text(string name) => File.ReadAllText(Path.Combine(Root, name));
        public void Dispose() { try { Directory.Delete(Root, true); } catch { } }
    }

    [Fact]
    public void Read_uses_snow_combos_and_ignores_leftover_ButtonA_in_config()
    {
        using var tmp = new TempKeys();
        var state = Writer().Read(tmp.Root);
        Assert.True(state.Client.Enabled);
        Assert.Equal("W", state.Client.Up);
        Assert.Equal("J", state.Client.ButtonA);
        Assert.Equal("U", state.Client.ButtonAB);
        Assert.Equal("T", state.Client.ButtonABC);
        Assert.Equal("Return", state.Client.Start);
        Assert.Equal("F2", state.Client.Service);
        Assert.Equal("UpArr", state.Lm.Up);
        Assert.Equal("Z", state.Lm.ButtonA);
        Assert.Null(state.Lm.ButtonAB);
        Assert.Equal("Q", state.Lm.Start);
        Assert.Null(state.Lm.Service);
        Assert.DoesNotContain("1 (NumPad)", state.Client.ButtonA);
    }

    [Fact]
    public void Read_infers_combos_when_snow_is_absent()
    {
        using var tmp = new TempKeys(snow: null);
        var state = Writer().Read(tmp.Root);
        Assert.Equal("J", state.Client.ButtonA);
        Assert.Equal("U", state.Client.ButtonAB);
        Assert.Equal("Y", state.Client.ButtonAC);
        Assert.Equal("T", state.Client.ButtonABC);
        Assert.Equal("Z", state.Lm.ButtonA);
    }

    [Fact]
    public void Apply_writes_expanded_lists_and_preserves_unknown_keys()
    {
        using var tmp = new TempKeys();
        var state = Writer().Read(tmp.Root);
        state.Client.ButtonA = "N";
        state.Client.Start = "Enter"; // alias; must be written as Return
        state.Lm.Up = "W";
        var report = Writer().Apply(tmp.Root, state, syncSnowLauncher: true);
        Assert.Contains(report.Written, p => p.EndsWith("config.ini"));
        Assert.Contains(report.Written, p => p.EndsWith("lmnfig.ini"));
        Assert.Contains(report.Written, p => p.EndsWith("SnowLauncher.ini"));
        Assert.NotEmpty(report.Backups);

        var cfg = tmp.Text("config.ini");
        Assert.Contains("A =N,U,Y,T", cfg);
        Assert.Contains("Start =Return", cfg);
        Assert.DoesNotContain("Start =Enter", cfg);
        Assert.Contains("ButtonA=1 (NumPad)", cfg);
        Assert.Contains("ButtonR1=E", cfg);
        Assert.Contains("ButtonL2=4 (NumPad)", cfg);
        Assert.Contains("ArcadeButton1=4", cfg);
        Assert.DoesNotContain("ButtonAB=", cfg.Substring(cfg.IndexOf("[keyboard]", StringComparison.Ordinal)));

        var lmn = tmp.Text("lmnfig.ini");
        Assert.Contains("Up=W", lmn);
        Assert.Contains("A=Z", lmn);
        Assert.DoesNotContain("Service=", lmn);

        var snow = tmp.Text("SnowLauncher.ini");
        Assert.Contains("[Game_Client_Keyboard]", snow);
        Assert.Contains("ButtonA=N", snow);
        Assert.Contains("ButtonAB=U", snow);
        Assert.Contains("Start=Return", snow);
        Assert.Contains("[Game_LM_Keyboard]", snow);
        Assert.Contains("Up=W", snow);
        Assert.Contains("ButtonA=Z", snow);
    }

    [Fact]
    public void Apply_can_skip_snow_and_still_write_game_ini()
    {
        using var tmp = new TempKeys();
        var before = tmp.Text("SnowLauncher.ini");
        var state = Writer().Read(tmp.Root);
        state.Client.Coin = "6";
        var report = Writer().Apply(tmp.Root, state, syncSnowLauncher: false);
        Assert.Contains(report.Written, p => p.EndsWith("config.ini"));
        Assert.DoesNotContain(report.Written, p => p.EndsWith("SnowLauncher.ini"));
        Assert.Equal(before, tmp.Text("SnowLauncher.ini"));
        Assert.Contains("Coin =6", tmp.Text("config.ini"));
    }

    [Fact]
    public void Apply_roundtrip_of_unchanged_pack_defaults_is_a_no_op()
    {
        using var tmp = new TempKeys();
        var beforeCfg = tmp.Text("config.ini");
        var beforeLm = tmp.Text("lmnfig.ini");
        var beforeSnow = tmp.Text("SnowLauncher.ini");
        var state = Writer().Read(tmp.Root);
        var report = Writer().Apply(tmp.Root, state, syncSnowLauncher: true);
        Assert.Empty(report.Written);
        Assert.Equal(beforeCfg, tmp.Text("config.ini"));
        Assert.Equal(beforeLm, tmp.Text("lmnfig.ini"));
        Assert.Equal(beforeSnow, tmp.Text("SnowLauncher.ini"));
    }

    [Fact]
    public void Apply_rejects_an_unknown_key_name()
    {
        using var tmp = new TempKeys();
        var state = Writer().Read(tmp.Root);
        state.Client.Up = "LShift";
        var ex = Assert.Throws<ArgumentException>(() => Writer().Apply(tmp.Root, state, true));
        Assert.Contains("LShift", ex.Message);
        Assert.Equal(Config, tmp.Text("config.ini"));
    }
}

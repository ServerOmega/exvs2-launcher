using Exvs2Launcher.Core.Files;

namespace Exvs2Launcher.Core.Tests;

public class DisplayConfigTests
{
    private static DisplayConfigWriter Writer() =>
        new(new BackupService(() => new DateTime(2026, 11, 15, 16, 40, 0)));

    private sealed class DisplayFolder : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "exvs2-display-" + Guid.NewGuid().ToString("N"));

        public const string Config =
            "[config]\r\nmode =Client\r\nserial =jZJj\r\n# Boot the game in windowed mode or not\r\n" +
            "Windowed =True\r\nRegion =1\r\nBorderlessWindow=False\r\nOpeningScreenSkip=SkipAll\r\n" +
            "IpAddress=192.168.1.69\r\n\r\n[keyboard]\r\nA =J\r\n\r\n[display]\r\nResolution=480p\r\nFramerateLimit=True\r\n";

        public const string Lm =
            "[config]\r\nBorderlessWindow=False\r\nWindowed=True\r\nOpeningScreenSkip=SkipAll\r\n" +
            "Mode=LM\r\nIpAddress=192.168.1.69\r\n[display]\r\nResolution=144p\r\nFramerateLimit=True\r\n";

        public const string Snow =
            "[Game_Config]\r\nSerial=jZJj\r\nOpeningScreenSkip=3\r\nPCB_Resolution=0\r\nLM_Resolution=3\r\nFramerateLimit=True\r\n";

        public DisplayFolder()
        {
            Directory.CreateDirectory(Root);
            File.WriteAllText(P("config.ini"), Config);
            File.WriteAllText(P("lmnfig.ini"), Lm);
            File.WriteAllText(P("SnowLauncher.ini"), Snow);
        }

        public string P(string name) => Path.Combine(Root, name);
        public string Text(string name) => File.ReadAllText(P(name));
        public void Dispose() { try { Directory.Delete(Root, true); } catch { } }
    }

    [Fact]
    public void Known_resolution_list_includes_observed_and_documented_tokens()
    {
        foreach (var token in new[] { "144p", "240p", "480p", "720p", "1080p", "2k", "4k", "8k" })
            Assert.True(DisplayOptions.IsKnownResolution(token), token);
        Assert.Equal("480p", DisplayOptions.CanonicalResolution("480P"));
        Assert.False(DisplayOptions.IsKnownResolution("900p"));
        Assert.Contains("900p", DisplayOptions.ResolutionChoices("900p"));
        Assert.True(DisplayOptions.RequiresBorderlessWindowed("8k"));
        Assert.False(DisplayOptions.RequiresBorderlessWindowed("480p"));
    }

    [Fact]
    public void Opening_skip_tokens_and_observed_snowlauncher_mapping()
    {
        Assert.Equal("SkipAll", DisplayOptions.CanonicalOpeningScreenSkip("skipall"));
        Assert.Equal("3", DisplayOptions.SnowLauncherOpeningFromGame("SkipAll"));
        Assert.Null(DisplayOptions.SnowLauncherOpeningFromGame("None"));
        Assert.Null(DisplayOptions.SnowLauncherOpeningFromGame("SkipReminder"));
        foreach (var token in new[] { "None", "SkipReminder", "SkipBrand", "SkipAll" })
            Assert.True(DisplayOptions.IsKnownOpeningScreenSkip(token), token);
    }

    [Fact]
    public void Read_keeps_client_and_lm_resolutions_separate()
    {
        using var g = new DisplayFolder();
        var state = Writer().Read(g.Root);
        Assert.Equal("480p", state.Client.Resolution);
        Assert.Equal("144p", state.Lm.Resolution);
        Assert.True(state.Client.Windowed);
        Assert.True(state.Lm.Windowed);
        Assert.False(state.Client.BorderlessWindow);
        Assert.True(state.Client.FramerateLimit);
        Assert.Equal("SkipAll", state.Client.OpeningScreenSkip);
        Assert.Contains("480p", state.Client.Summary);
        Assert.Contains("144p", state.Lm.Summary);
    }

    [Fact]
    public void Apply_changes_only_client_resolution_and_backs_up()
    {
        using var g = new DisplayFolder();
        var state = Writer().Read(g.Root);
        state.Client.Resolution = "1080p";
        var report = Writer().Apply(g.Root, state, syncSnowLauncher: true);

        Assert.Contains(g.P("config.ini"), report.Written);
        Assert.DoesNotContain(g.P("lmnfig.ini"), report.Written);
        Assert.True(File.Exists(g.P("config.ini.bak-20261115-164000")));
        Assert.Equal(DisplayFolder.Config, File.ReadAllText(g.P("config.ini.bak-20261115-164000")));
        Assert.Contains("Resolution=1080p", g.Text("config.ini"));
        Assert.Equal(DisplayFolder.Lm, g.Text("lmnfig.ini"));
        Assert.Contains("A =J", g.Text("config.ini"));
        Assert.Contains("serial =jZJj", g.Text("config.ini"));
        Assert.Contains("PCB_Resolution=0", g.Text("SnowLauncher.ini"));
        Assert.Contains("LM_Resolution=3", g.Text("SnowLauncher.ini"));
    }

    [Fact]
    public void Apply_can_set_lm_resolution_independently()
    {
        using var g = new DisplayFolder();
        var state = Writer().Read(g.Root);
        state.Lm.Resolution = "240p";
        Writer().Apply(g.Root, state, syncSnowLauncher: false);
        Assert.Contains("Resolution=480p", g.Text("config.ini"));
        Assert.Contains("Resolution=240p", g.Text("lmnfig.ini"));
        Assert.Equal(DisplayFolder.Snow, g.Text("SnowLauncher.ini"));
    }

    [Fact]
    public void Apply_writes_window_framerate_and_skip_on_both_files()
    {
        using var g = new DisplayFolder();
        var state = Writer().Read(g.Root);
        state.Client.Windowed = true;
        state.Client.BorderlessWindow = true;
        state.Client.FramerateLimit = false;
        state.Client.OpeningScreenSkip = "None";
        state.Lm.Windowed = true;
        state.Lm.BorderlessWindow = true;
        state.Lm.FramerateLimit = false;
        state.Lm.OpeningScreenSkip = "None";
        var report = Writer().Apply(g.Root, state, syncSnowLauncher: true);

        Assert.Contains("BorderlessWindow=True", g.Text("config.ini"));
        Assert.Contains("FramerateLimit=False", g.Text("config.ini"));
        Assert.Contains("OpeningScreenSkip=None", g.Text("config.ini"));
        Assert.Contains("BorderlessWindow=True", g.Text("lmnfig.ini"));
        Assert.Contains("OpeningScreenSkip=None", g.Text("lmnfig.ini"));
        Assert.Contains("FramerateLimit=False", g.Text("SnowLauncher.ini"));
        Assert.Contains("OpeningScreenSkip=3", g.Text("SnowLauncher.ini")); // left alone; mapping unknown for None
        Assert.Contains(report.Notes, n => n.Contains("OpeningScreenSkip was left alone"));
        Assert.Contains(report.Notes, n => n.Contains("PCB_Resolution"));
    }

    [Fact]
    public void Same_values_write_nothing()
    {
        using var g = new DisplayFolder();
        var report = Writer().Apply(g.Root, Writer().Read(g.Root), syncSnowLauncher: true);
        Assert.Empty(report.Written);
        Assert.Empty(Directory.GetFiles(g.Root, "*.bak-*"));
        Assert.Equal(DisplayFolder.Config, g.Text("config.ini"));
    }

    [Fact]
    public void Apply_preserves_spacing_around_existing_keys()
    {
        using var g = new DisplayFolder();
        var state = Writer().Read(g.Root);
        state.Client.Windowed = false;
        Writer().Apply(g.Root, state, false);
        Assert.Contains("Windowed =False", g.Text("config.ini"));
    }

    [Fact]
    public void Missing_config_ini_throws()
    {
        using var g = new DisplayFolder();
        File.Delete(g.P("config.ini"));
        var ex = Assert.Throws<FileNotFoundException>(() => Writer().Apply(g.Root, new DisplayFileState(), false));
        Assert.Contains("config.ini", ex.Message);
    }
}

using System.Net.NetworkInformation;
using Exvs2Launcher.Core.Files;
using Exvs2Launcher.Core.Launch;
using Exvs2Launcher.Core.Net;
using Exvs2Launcher.Core.Profiles;

namespace Exvs2Launcher.Core.Tests;

public class CabLauncherTests
{
    private static readonly NicAddress Lan = new("Ethernet", "Realtek", "192.168.1.80", NetworkInterfaceType.Ethernet, true, false);
    private static readonly NicAddress LmSecondary = new("Ethernet", "Realtek", "192.168.1.245", NetworkInterfaceType.Ethernet, true, false);
    private static readonly NicAddress ExistingLm = new("Ethernet", "Realtek", "192.168.1.69", NetworkInterfaceType.Ethernet, true, false);

    private static (CabLauncher, FakeWorld, List<string>) Make(
        TempGameFolder g, IReadOnlyList<string>? conflicts = null, IReadOnlyList<NicAddress>? nics = null)
    {
        var w = new FakeWorld();
        w.Files.Add(Path.Combine(g.Root, "3-Start-Server.bat"));
        w.Files.Add(Path.Combine(g.Root, "vsac29_Release.exe"));
        var seq = new StartSequencer(w, w, w, w, w.Files.Contains);
        var launcher = new CabLauncher(seq, new GameConfigWriter(), () => conflicts ?? Array.Empty<string>(),
            () => nics ?? new[] { Lan, ExistingLm, LmSecondary });
        var log = new List<string>();
        launcher.Log += log.Add;
        seq.Log += log.Add;
        return (launcher, w, log);
    }

    [Fact]
    public async Task Start_writes_detected_pcb_ip_but_keeps_existing_lm_ip()
    {
        using var g = new TempGameFolder();
        // lmnfig starts at 192.168.1.69, which is a real address on the adapter. Auto-detect must not overwrite it
        // with the detected PCB IP (.80).
        var (launcher, w, log) = Make(g);
        var p = new LauncherProfile { Name = "Test", GameFolder = g.Root, CardServerPath = Path.Combine(g.Root, "3-Start-Server.bat") };
        var r = await launcher.StartAsync(new ProfileFile { Profiles = { p } }, p);

        Assert.True(r.Succeeded, r.Message);
        Assert.Contains("IpAddress=192.168.1.80", g.Text("config.ini"));
        Assert.Contains("IpAddress=192.168.1.69", g.Text("lmnfig.ini"));
        Assert.DoesNotContain("IpAddress=192.168.1.80", g.Text("lmnfig.ini"));
        Assert.Contains("PCB_IpAddress=192.168.1.80", g.Text("SnowLauncher.ini"));
        Assert.Contains("LM_IpAddress=192.168.1.69", g.Text("SnowLauncher.ini"));
        Assert.Single(g.Backups("config.ini"));
        Assert.Equal("192.168.1.80", p.PcbIp);
        Assert.Equal("192.168.1.69", p.LmIp);
        Assert.Contains(log, l => l.Contains("lmnfig.ini IpAddress left as 192.168.1.69"));
        Assert.Contains(log, l => l.Contains("This PC's LAN IP (PCB):") && l.Contains("192.168.1.80"));
        Assert.Equal(new[] { "Card server", "Client" }, r.Started.Select(x => x.Role));
    }

    [Fact]
    public async Task Start_keeps_secondary_lm_address_and_syncs_snowlauncher()
    {
        using var g = new TempGameFolder();
        File.WriteAllText(g.P("lmnfig.ini"), TempGameFolder.Lm.Replace("192.168.1.69", "192.168.1.245"));
        var (launcher, _, log) = Make(g);
        var p = new LauncherProfile { Name = "Test", GameFolder = g.Root, CardServerPath = Path.Combine(g.Root, "3-Start-Server.bat"), SyncSnowLauncherIni = true };
        var r = await launcher.StartAsync(new ProfileFile { Profiles = { p } }, p);

        Assert.True(r.Succeeded, r.Message);
        Assert.Contains("IpAddress=192.168.1.80", g.Text("config.ini"));
        Assert.Contains("IpAddress=192.168.1.245", g.Text("lmnfig.ini"));
        Assert.Contains("LM_IpAddress=192.168.1.245", g.Text("SnowLauncher.ini"));
        Assert.Contains("PCB_IpAddress=192.168.1.80", g.Text("SnowLauncher.ini"));
        Assert.Equal("192.168.1.245", p.LmIp);
        Assert.Contains(log, l => l.Contains("lmnfig.ini IpAddress left as 192.168.1.245"));
        Assert.Contains(log, l => l.Contains("SnowLauncher.ini LM_IpAddress: 192.168.1.69 → 192.168.1.245"));
    }

    [Fact]
    public async Task Start_refused_while_snowlauncher_runs_and_nothing_written()
    {
        using var g = new TempGameFolder();
        var (launcher, w, _) = Make(g, new[] { "SnowLauncher.exe" });
        var p = new LauncherProfile { GameFolder = g.Root, CardServerPath = Path.Combine(g.Root, "3-Start-Server.bat") };
        var r = await launcher.StartAsync(new ProfileFile { Profiles = { p } }, p);
        Assert.Equal(StartStage.Failed, r.FinalStage);
        Assert.Contains("SnowLauncher.exe", r.Message);
        Assert.Equal(TempGameFolder.Config, g.Text("config.ini"));
        Assert.Empty(w.Events);
    }

    [Fact]
    public async Task Start_warns_about_duplicate_serial()
    {
        using var a = new TempGameFolder();
        using var b = new TempGameFolder();
        var (launcher, _, log) = Make(a);
        var pa = new LauncherProfile { Name = "A", GameFolder = a.Root, CardServerPath = Path.Combine(a.Root, "3-Start-Server.bat"), AutoDetectIp = false };
        var pb = new LauncherProfile { Name = "B", GameFolder = b.Root };
        await launcher.StartAsync(new ProfileFile { Profiles = { pa, pb } }, pa);
        Assert.Contains(log, l => l.Contains("also used by B"));
    }
}

using Exvs2Launcher.Core.Launch;
using Exvs2Launcher.Core.Profiles;

namespace Exvs2Launcher.Core.Tests;

internal sealed class FakeCabs : ICabControl
{
    public List<string> Calls { get; } = new();
    public HashSet<(string, string)> Running { get; } = new();
    public HashSet<(string, string)> FailStart { get; } = new();
    public HashSet<string> FailPrepare { get; } = new();

    public void Prepare(LauncherProfile p)
    {
        Calls.Add($"prepare:{p.Name}");
        if (FailPrepare.Contains(p.Name)) throw new IOException("locked");
    }

    public Task<StartOutcome> StartRoleAsync(LauncherProfile p, string role, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        Calls.Add($"start:{p.Name}:{role}");
        if (FailStart.Contains((p.Name, role)))
            return Task.FromResult(new StartOutcome(StartStage.Failed, "boom", Array.Empty<IStartedProcess>()));
        Running.Add((p.Name, role));
        return Task.FromResult(new StartOutcome(StartStage.Running, "ok", Array.Empty<IStartedProcess>()));
    }

    public ComponentStatus Status(LauncherProfile p, string role) =>
        new(role, Running.Contains((p.Name, role)), Running.Contains((p.Name, role)) ? 1 : null, null);

    public int Stop(LauncherProfile p, string role)
    {
        Calls.Add($"stop:{p.Name}:{role}");
        return Running.Remove((p.Name, role)) ? 1 : 0;
    }
}

internal sealed class RecordingDelay : IDelay
{
    private readonly List<string> _calls;
    public RecordingDelay(List<string> calls) => _calls = calls;
    public Task WaitAsync(TimeSpan d, CancellationToken ct) { ct.ThrowIfCancellationRequested(); _calls.Add($"wait:{d.TotalSeconds}"); return Task.CompletedTask; }
}

public class MultiCabTests
{
    private static List<LauncherProfile> FourCabs(string? sharedServer = "/mm/server/Server-Infinite.exe") => new()
    {
        new() { Name = "V29", GameFolder = "/g/EXVS2IB_V29", CardServerPath = sharedServer, StartCardServer = true, StartLm = true, StartClient = true },
        new() { Name = "P2", GameFolder = "/g/EXVS2IB_V29_P2", CardServerPath = sharedServer, StartCardServer = true, StartClient = true },
        new() { Name = "P3", GameFolder = "/g/EXVS2IB_V29_P3", CardServerPath = sharedServer, StartCardServer = true, StartClient = true },
        new() { Name = "P4", GameFolder = "/g/EXVS2IB_V29_P4", CardServerPath = sharedServer, StartCardServer = true, StartClient = true },
    };

    private static (MultiCabOrchestrator O, FakeCabs Cabs, List<string> Calls) Make(Func<IReadOnlyList<string>>? blockers = null)
    {
        var cabs = new FakeCabs();
        var o = new MultiCabOrchestrator(cabs, new RecordingDelay(cabs.Calls), blockers ?? (() => Array.Empty<string>()));
        return (o, cabs, cabs.Calls);
    }

    [Fact]
    public async Task Order_is_files_then_server_once_then_lm_then_staggered_clients()
    {
        var (o, _, calls) = Make();
        var r = await o.StartEverythingAsync(FourCabs(), new LauncherSettings { LmToClientsDelaySeconds = 5, ClientStaggerSeconds = 8 });
        Assert.True(r.Succeeded, r.Message);
        Assert.Equal(new[]
        {
            "prepare:V29", "prepare:P2", "prepare:P3", "prepare:P4",
            "start:V29:Card server",
            "start:V29:LM",
            "wait:5",
            "start:V29:Client", "wait:8", "start:P2:Client", "wait:8", "start:P3:Client", "wait:8", "start:P4:Client",
        }, calls);
    }

    [Fact]
    public async Task Two_distinct_servers_both_start_before_any_lm()
    {
        var cabs = FourCabs();
        cabs[2].CardServerPath = "/mm2/server/Server-Infinite.exe";
        cabs[2].CardServerPort = 28090;
        var (o, _, calls) = Make();
        await o.StartEverythingAsync(cabs, new LauncherSettings { LmToClientsDelaySeconds = 0, ClientStaggerSeconds = 0 });
        var starts = calls.Where(c => c.StartsWith("start:")).ToList();
        Assert.Equal("start:V29:Card server", starts[0]);
        Assert.Equal("start:P3:Card server", starts[1]);
        Assert.Equal("start:V29:LM", starts[2]);
        Assert.DoesNotContain(calls, c => c.StartsWith("wait:"));
    }

    [Fact]
    public async Task Server_failure_skips_that_servers_cabs_but_not_others()
    {
        var cabs = FourCabs();
        cabs[3].CardServerPath = "/other/Server-Infinite.exe";
        var (o, fake, calls) = Make();
        fake.FailStart.Add(("V29", ComponentRoles.CardServer));
        var lights = new List<string>();
        o.LightChanged += (cab, role, l) => lights.Add($"{cab}:{role}:{l}");
        var r = await o.StartEverythingAsync(cabs, new LauncherSettings());
        Assert.False(r.Succeeded);
        Assert.DoesNotContain("start:V29:LM", calls);
        Assert.DoesNotContain("start:P2:Client", calls);
        Assert.Contains("start:P4:Client", calls);
        Assert.Contains("P2:Card server:Failed", lights);
        Assert.Contains("P2:Client:Skipped", lights);
    }

    [Fact]
    public async Task Running_cab_is_not_prepared_and_not_restarted_and_no_stagger_before_it()
    {
        var (o, fake, calls) = Make();
        fake.Running.Add(("P2", ComponentRoles.Client));
        await o.StartEverythingAsync(FourCabs(), new LauncherSettings { LmToClientsDelaySeconds = 0, ClientStaggerSeconds = 8 });
        Assert.DoesNotContain("prepare:P2", calls);
        // P2's client is "started" through the sequencer, which reports "already running" without a new process;
        // no stagger wait is spent on it.
        int i = calls.IndexOf("start:P2:Client");
        Assert.NotEqual("wait:8", calls[i - 1]);
    }

    [Fact]
    public async Task Snowlauncher_running_blocks_everything()
    {
        var (o, _, calls) = Make(() => new[] { "SnowLauncher.exe" });
        var r = await o.StartEverythingAsync(FourCabs(), new LauncherSettings());
        Assert.False(r.Succeeded);
        Assert.Empty(calls);
    }

    [Fact]
    public async Task Excluded_profiles_are_left_alone()
    {
        var cabs = FourCabs();
        cabs[1].IncludeInStartEverything = false;
        var (o, _, calls) = Make();
        await o.StartEverythingAsync(cabs, new LauncherSettings());
        Assert.DoesNotContain(calls, c => c.Contains(":P2"));
    }

    [Fact]
    public async Task Prepare_failure_skips_that_cab_only()
    {
        var (o, fake, calls) = Make();
        fake.FailPrepare.Add("P3");
        var r = await o.StartEverythingAsync(FourCabs(), new LauncherSettings());
        Assert.False(r.Succeeded);
        Assert.DoesNotContain("start:P3:Client", calls);
        Assert.Contains("start:P4:Client", calls);
    }

    [Fact]
    public async Task Cancel_stops_further_starts()
    {
        var (o, fake, calls) = Make();
        using var cts = new CancellationTokenSource();
        o.LightChanged += (cab, role, l) => { if (cab == "V29" && role == ComponentRoles.Lm && l == CabLight.Running) cts.Cancel(); };
        var r = await o.StartEverythingAsync(FourCabs(), new LauncherSettings(), cts.Token);
        Assert.True(r.Cancelled);
        Assert.DoesNotContain(calls, c => c.EndsWith(":Client"));
    }

    [Fact]
    public void Stop_everything_goes_clients_then_lm_then_servers()
    {
        var (o, fake, calls) = Make();
        foreach (var p in FourCabs()) { fake.Running.Add((p.Name, ComponentRoles.Client)); }
        fake.Running.Add(("V29", ComponentRoles.Lm));
        fake.Running.Add(("V29", ComponentRoles.CardServer));
        int n = o.StopEverything(FourCabs());
        Assert.Equal(6, n);
        int lastClient = calls.FindLastIndex(c => c.EndsWith(":Client"));
        int firstLm = calls.FindIndex(c => c.EndsWith(":LM"));
        int lastLm = calls.FindLastIndex(c => c.EndsWith(":LM"));
        int firstServer = calls.FindIndex(c => c.EndsWith(":Card server"));
        Assert.True(lastClient < firstLm && lastLm < firstServer);
        Assert.Single(calls, c => c.EndsWith(":Card server"));
    }

    [Fact]
    public void Plan_groups_servers_by_path_and_port()
    {
        var cabs = FourCabs();
        cabs[1].CardServerPort = 28081;
        var plan = MultiCabPlan.Build(cabs);
        Assert.Equal(2, plan.Servers.Count);
        Assert.Single(plan.Lms);
        Assert.Equal(4, plan.Clients.Count);
    }

    [Fact]
    public async Task Real_sequencer_never_starts_stock_server_through_start_everything()
    {
        var world = new FakeWorld();
        world.Files.Add("/g/A/server/Server.exe");
        var file = new ProfileFile();
        var p = new LauncherProfile { Name = "A", GameFolder = "/g/A", CardServerPath = "/g/A/server/Server.exe", StartLm = false, StartClient = false };
        file.Profiles.Add(p);
        var control = new SequencerCabControl(() => file,
            () => new StartSequencer(world, world, world, world, world.Files.Contains, new EmptyProcesses()), new Files.GameConfigWriter());
        var o = new MultiCabOrchestrator(control, world, () => Array.Empty<string>());
        var r = await o.StartEverythingAsync(file.Profiles, new LauncherSettings());
        Assert.False(r.Succeeded);
        Assert.DoesNotContain(world.Events, e => e.StartsWith("start:"));
    }

    private sealed class EmptyProcesses : IProcessControl
    {
        public IReadOnlyList<RunningProcessInfo> List() => Array.Empty<RunningProcessInfo>();
        public void KillTree(int pid) { }
    }
}

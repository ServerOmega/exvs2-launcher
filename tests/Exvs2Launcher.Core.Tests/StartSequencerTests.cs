using Exvs2Launcher.Core.Launch;

namespace Exvs2Launcher.Core.Tests;

internal sealed class FakeWorld : IProcessStarter, IPortProbe, IPortOwnerLookup, IDelay
{
    public List<string> Events { get; } = new();
    public bool Listening { get; set; }
    public PortOwner? Owner { get; set; }
    /// <summary>The port opens after this many probes once the card server has started (-1 = never).</summary>
    public int ProbesUntilListening { get; set; } = 2;
    public bool ServerExitsImmediately { get; set; }
    public HashSet<string> Files { get; } = new(StringComparer.OrdinalIgnoreCase);
    private int _probesAfterStart = -1;
    private int _nextPid = 100;
    public List<FakeProcess> Processes { get; } = new();

    public IStartedProcess Start(LaunchSpec spec)
    {
        Events.Add($"start:{spec.Role}:{Path.GetFileName(spec.FileName)}@{spec.WorkingDirectory}");
        var p = new FakeProcess(spec.Role, _nextPid++);
        if (spec.Role == "Card server")
        {
            _probesAfterStart = 0;
            if (ServerExitsImmediately) { p.Exited = true; p.Code = 1; }
        }
        Processes.Add(p);
        return p;
    }

    public Task<bool> IsListeningAsync(string host, int port, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (_probesAfterStart >= 0 && !Listening)
        {
            if (ProbesUntilListening >= 0 && _probesAfterStart >= ProbesUntilListening)
            {
                Listening = true;
                Owner = new PortOwner(4242, "Server-Infinite", @"C:\mm\server\Server-Infinite.exe");
            }
            _probesAfterStart++;
        }
        Events.Add($"probe:{host}:{port}={Listening}");
        return Task.FromResult(Listening);
    }

    public PortOwner? FindListener(int port) => Listening ? Owner : null;

    public Task WaitAsync(TimeSpan duration, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        Events.Add($"wait:{duration.TotalMilliseconds}");
        return Task.CompletedTask;
    }
}

internal sealed class FakeProcess : IStartedProcess
{
    public FakeProcess(string role, int id) { Role = role; Id = id; }
    public string Role { get; }
    public int Id { get; }
    public bool Exited { get; set; }
    public int? Code { get; set; }
    public bool Killed { get; private set; }
    public bool HasExited => Exited;
    public int? ExitCode => Code;
    public void Kill() { Killed = true; Exited = true; }
}

public class StartSequencerTests
{
    private const string Game = "/games/Test";
    private const string Bat = "/mm/3-Start-Server.bat";

    private static (StartSequencer, FakeWorld) Make(Action<FakeWorld>? setup = null, IProcessControl? processes = null)
    {
        var w = new FakeWorld();
        w.Files.Add(Bat);
        w.Files.Add(Path.Combine(Game, "vsac29_Release.exe"));
        w.Files.Add(Path.Combine(Game, "vsac29_Release_LM.exe"));
        setup?.Invoke(w);
        var seq = new StartSequencer(w, w, w, w, w.Files.Contains, processes);
        return (seq, w);
    }

    private static StartPlan Plan(bool lm = true, bool client = true, bool server = true, string? path = Bat) => new()
    {
        GameFolder = Game,
        CardServerPath = path,
        StartCardServer = server,
        StartLm = lm,
        StartClient = client,
        PortTimeout = TimeSpan.FromSeconds(5),
        PollInterval = TimeSpan.FromSeconds(1),
        LmToClientDelay = TimeSpan.FromSeconds(3),
    };

    [Fact]
    public async Task Starts_server_waits_for_port_then_lm_then_client()
    {
        var (seq, w) = Make();
        var stages = new List<StartStage>();
        seq.StageChanged += stages.Add;

        var r = await seq.RunAsync(Plan());

        Assert.True(r.Succeeded, r.Message);
        var starts = w.Events.Where(e => e.StartsWith("start:")).ToList();
        Assert.Equal(new[]
        {
            $"start:Card server:3-Start-Server.bat@{Path.GetFullPath("/mm")}",
            $"start:LM:vsac29_Release_LM.exe@{Path.GetFullPath(Game)}",
            $"start:Client:vsac29_Release.exe@{Path.GetFullPath(Game)}",
        }, starts);

        int serverIdx = w.Events.IndexOf(starts[0]);
        int lmIdx = w.Events.IndexOf(starts[1]);
        int lastFalseProbe = w.Events.FindLastIndex(e => e.EndsWith("=False"));
        int firstTrueProbe = w.Events.FindIndex(e => e.EndsWith("=True"));
        Assert.True(serverIdx < lastFalseProbe && lastFalseProbe < firstTrueProbe && firstTrueProbe < lmIdx);
        Assert.Equal("wait:3000", w.Events[lmIdx + 1]);

        Assert.Equal(new[]
        {
            StartStage.Validating, StartStage.CheckingPort, StartStage.StartingCardServer, StartStage.WaitingForPort,
            StartStage.StartingLm, StartStage.WaitingBeforeClient, StartStage.StartingClient, StartStage.Running,
        }, stages);
        Assert.Equal(3, r.Started.Count);
    }

    [Fact]
    public async Task Lm_disabled_starts_only_server_and_client_without_delay()
    {
        var (seq, w) = Make();
        var r = await seq.RunAsync(Plan(lm: false));
        Assert.True(r.Succeeded);
        Assert.DoesNotContain(w.Events, e => e.Contains("LM"));
        Assert.DoesNotContain("wait:3000", w.Events);
        Assert.Equal(new[] { "Card server", "Client" }, r.Started.Select(p => p.Role));
    }

    [Fact]
    public async Task Patched_server_already_listening_is_reused()
    {
        var (seq, w) = Make(x => { x.Listening = true; x.Owner = new PortOwner(77, "Server-Infinite", @"C:\x\Server-Infinite.exe"); });
        string log = "";
        seq.Log += m => log += m + "\n";
        var r = await seq.RunAsync(Plan());
        Assert.True(r.Succeeded);
        Assert.DoesNotContain(w.Events, e => e.StartsWith("start:Card server"));
        Assert.Contains("already listening", log);
        Assert.Contains("pid 77", log);
    }

    [Fact]
    public async Task Stock_server_on_port_is_refused()
    {
        var (seq, w) = Make(x => { x.Listening = true; x.Owner = new PortOwner(55, "Server", @"E:\Games\Test\server\Server.exe"); });
        var r = await seq.RunAsync(Plan());
        Assert.Equal(StartStage.Failed, r.FinalStage);
        Assert.Contains("stock card server", r.Message);
        Assert.Contains("pid 55", r.Message);
        Assert.DoesNotContain(w.Events, e => e.StartsWith("start:"));
    }

    [Fact]
    public async Task Other_program_on_port_is_refused_and_named()
    {
        var (seq, w) = Make(x => { x.Listening = true; x.Owner = new PortOwner(9, "nginx", null); });
        var r = await seq.RunAsync(Plan());
        Assert.Equal(StartStage.Failed, r.FinalStage);
        Assert.Contains("nginx", r.Message);
        Assert.Empty(r.Started);
    }

    [Fact]
    public async Task Unidentified_listener_is_refused()
    {
        var (seq, _) = Make(x => { x.Listening = true; x.Owner = null; });
        var r = await seq.RunAsync(Plan());
        Assert.Equal(StartStage.Failed, r.FinalStage);
        Assert.Contains("could not tell", r.Message);
    }

    [Fact]
    public async Task Timeout_fails_and_does_not_start_game()
    {
        var (seq, w) = Make(x => x.ProbesUntilListening = -1);
        var r = await seq.RunAsync(Plan());
        Assert.Equal(StartStage.Failed, r.FinalStage);
        Assert.Contains("did not open", r.Message);
        Assert.Equal(5, w.Events.Count(e => e == "wait:1000"));
        Assert.DoesNotContain(w.Events, e => e.StartsWith("start:LM") || e.StartsWith("start:Client"));
    }

    [Fact]
    public async Task Server_that_exits_early_fails_fast()
    {
        var (seq, w) = Make(x => { x.ProbesUntilListening = -1; x.ServerExitsImmediately = true; });
        var r = await seq.RunAsync(Plan());
        Assert.Equal(StartStage.Failed, r.FinalStage);
        Assert.Contains("exited (code 1)", r.Message);
        Assert.DoesNotContain("wait:1000", w.Events);
    }

    [Fact]
    public async Task Stock_server_path_is_rejected_before_anything_runs()
    {
        var stock = Path.Combine(Game, "server", "Server.exe");
        var (seq, w) = Make(x => x.Files.Add(stock));
        var r = await seq.RunAsync(Plan(path: stock));
        Assert.Equal(StartStage.Failed, r.FinalStage);
        Assert.Contains("stock card server", r.Message);
        Assert.Empty(w.Events);
    }

    [Fact]
    public async Task Missing_files_are_reported()
    {
        var (seq, _) = Make(x => x.Files.Clear());
        var r = await seq.RunAsync(Plan());
        Assert.Contains("Card server not found", r.Message);
    }

    [Fact]
    public async Task Card_server_off_and_port_closed_fails()
    {
        var (seq, w) = Make();
        var r = await seq.RunAsync(Plan(server: false));
        Assert.Equal(StartStage.Failed, r.FinalStage);
        Assert.Contains("Nothing is listening", r.Message);
        Assert.DoesNotContain(w.Events, e => e.StartsWith("start:"));
    }

    [Fact]
    public async Task Card_server_off_and_patched_running_starts_game_only()
    {
        var (seq, w) = Make(x => { x.Listening = true; x.Owner = new PortOwner(1, "Server-Infinite", null); });
        var r = await seq.RunAsync(Plan(server: false, lm: false));
        Assert.True(r.Succeeded);
        Assert.Equal(new[] { "Client" }, r.Started.Select(p => p.Role));
    }

    [Fact]
    public async Task Cancel_while_waiting_stops_sequence()
    {
        var (seq, w) = Make(x => x.ProbesUntilListening = -1);
        using var cts = new CancellationTokenSource();
        seq.StageChanged += s => { if (s == StartStage.WaitingForPort) cts.Cancel(); };
        var r = await seq.RunAsync(Plan(), cts.Token);
        Assert.Equal(StartStage.Cancelled, r.FinalStage);
        Assert.DoesNotContain(w.Events, e => e.StartsWith("start:Client"));
    }

    [Fact]
    public async Task Stop_kills_game_but_can_keep_server_then_restart_reuses_it()
    {
        var (seq, w) = Make();
        await seq.RunAsync(Plan());
        Assert.Equal(2, seq.Stop(includeCardServer: false));
        Assert.False(w.Processes[0].Killed);
        Assert.True(w.Processes[1].Killed && w.Processes[2].Killed);
        Assert.Equal(StartStage.Idle, seq.Stage);

        var r = await seq.RunAsync(Plan());
        Assert.True(r.Succeeded);
        Assert.Single(w.Events, e => e.StartsWith("start:Card server"));

        Assert.Equal(3, seq.Stop(includeCardServer: true));
        Assert.True(w.Processes[0].Killed);
    }

    [Fact]
    public async Task Second_run_while_running_is_refused()
    {
        var (seq, _) = Make();
        await seq.RunAsync(Plan());
        var r = await seq.RunAsync(Plan());
        Assert.Contains("already", r.Message);
    }

    [Theory]
    [InlineData("Server-Infinite", null, PortOwnerKind.PatchedServer)]
    [InlineData("server-infinite", null, PortOwnerKind.PatchedServer)]
    [InlineData("Server", null, PortOwnerKind.StockServer)]
    [InlineData("MyRenamedServer", "/mm/server/MyRenamedServer.exe", PortOwnerKind.PatchedServer)]
    [InlineData("svchost", null, PortOwnerKind.Other)]
    public void Classifier(string name, string? path, PortOwnerKind expected)
    {
        var plan = new StartPlan { GameFolder = Game, CardServerPath = "/mm/server/MyRenamedServer.exe" };
        Assert.Equal(expected, PortOwnerClassifier.Classify(true, new PortOwner(1, name, path), plan));
        Assert.Equal(PortOwnerKind.None, PortOwnerClassifier.Classify(false, null, plan));
        Assert.Equal(PortOwnerKind.Unknown, PortOwnerClassifier.Classify(true, null, plan));
    }

    [Fact]
    public async Task StopRole_closes_only_that_component_and_folder_fallback()
    {
        var procs = new FakeProcessControl();
        var otherLm = Path.Combine("/games/Other", "vsac29_Release_LM.exe");
        procs.Running.Add(new RunningProcessInfo(500, otherLm, "vsac29_Release_LM"));
        var (seq, w) = Make(processes: procs);
        await seq.RunAsync(Plan());

        Assert.Equal(1, seq.StopRole(ComponentRoles.Lm, Plan()));
        Assert.True(w.Processes.Single(p => p.Role == "LM").Killed);
        Assert.False(w.Processes.Single(p => p.Role == "Client").Killed);
        Assert.False(w.Processes.Single(p => p.Role == "Card server").Killed);
        Assert.DoesNotContain(500, procs.Killed); // other folder's LM is left alone

        // Fallback: a client exe in this profile's folder that we did not start this session.
        var clientPath = Path.GetFullPath(Path.Combine(Game, "vsac29_Release.exe"));
        procs.Running.Add(new RunningProcessInfo(600, clientPath, "vsac29_Release"));
        // Mark the started client as already exited so we use the path fallback.
        w.Processes.Single(p => p.Role == "Client").Exited = true;
        Assert.Equal(1, seq.StopRole(ComponentRoles.Client, Plan()));
        Assert.Equal(new[] { 600 }, procs.Killed);

        var lmStatus = seq.RoleStatus(ComponentRoles.Lm, Plan());
        Assert.False(lmStatus.Running);
        var serverStatus = seq.RoleStatus(ComponentRoles.CardServer, Plan());
        Assert.True(serverStatus.Running);
    }

    [Fact]
    public async Task Start_lm_only_does_not_require_the_card_server_port()
    {
        var (seq, w) = Make();
        var plan = new StartPlan
        {
            GameFolder = Game, CardServerPath = Bat, StartCardServer = false, StartLm = true, StartClient = false,
            RequireCardServerPort = false, PortTimeout = TimeSpan.FromSeconds(5), PollInterval = TimeSpan.FromSeconds(1),
        };
        var r = await seq.RunAsync(plan);
        Assert.True(r.Succeeded, r.Message);
        Assert.Equal(new[] { "LM" }, r.Started.Select(p => p.Role));
        Assert.DoesNotContain(w.Events, e => e.StartsWith("start:Card server"));
    }
}

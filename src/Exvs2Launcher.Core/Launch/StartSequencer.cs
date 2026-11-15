using Exvs2Launcher.Core.Files;

namespace Exvs2Launcher.Core.Launch;

/// <summary>
/// Starts a cab in the order a self-hosted matchmaking setup needs:
/// patched card server → wait for 127.0.0.1:28080 → LM (if enabled) → client (if enabled).
/// It never starts the pack's stock <c>server\Server.exe</c>, and refuses to continue when something other than the
/// patched server holds the port.
/// </summary>
public sealed class StartSequencer
{
    private readonly IProcessStarter _starter;
    private readonly IPortProbe _probe;
    private readonly IPortOwnerLookup _owners;
    private readonly IDelay _delay;
    private readonly Func<string, bool> _fileExists;
    private readonly IProcessControl _processes;
    private readonly List<IStartedProcess> _started = new();

    public StartSequencer(IProcessStarter starter, IPortProbe probe, IPortOwnerLookup owners, IDelay delay,
        Func<string, bool>? fileExists = null, IProcessControl? processes = null)
    {
        _starter = starter;
        _probe = probe;
        _owners = owners;
        _delay = delay;
        _fileExists = fileExists ?? File.Exists;
        _processes = processes ?? new SystemProcessControl();
    }

    public StartStage Stage { get; private set; } = StartStage.Idle;
    public IReadOnlyList<IStartedProcess> Started => _started;

    public event Action<StartStage>? StageChanged;
    public event Action<string>? Log;

    public async Task<StartOutcome> RunAsync(StartPlan plan, CancellationToken ct = default)
    {
        if (Stage is not (StartStage.Idle or StartStage.Failed or StartStage.Cancelled))
            return new StartOutcome(Stage, "A start is already in progress or running.", _started.ToList());
        _started.RemoveAll(p => p.HasExited);

        try
        {
            Enter(StartStage.Validating);
            var problem = Validate(plan);
            if (problem is not null) return Fail(problem);

            var game = new GameFolder(plan.GameFolder);

            Enter(StartStage.CheckingPort);
            bool listening = await _probe.IsListeningAsync(plan.Host, plan.Port, ct).ConfigureAwait(false);
            var owner = _owners.FindListener(plan.Port);
            if (owner is not null) listening = true;
            var kind = PortOwnerClassifier.Classify(listening, owner, plan);
            string explain = PortOwnerClassifier.Describe(kind, plan.Port, owner);

            switch (kind)
            {
                case PortOwnerKind.StockServer:
                case PortOwnerKind.Other:
                case PortOwnerKind.Unknown:
                    return Fail(explain);
                case PortOwnerKind.PatchedServer:
                    if (!plan.ReuseRunningPatchedServer)
                        return Fail(explain);
                    Say(explain + " Using it.");
                    break;
                case PortOwnerKind.None:
                    if (!plan.StartCardServer)
                    {
                        if (plan.RequireCardServerPort)
                            return Fail($"Nothing is listening on {plan.Host}:{plan.Port} and starting the card server is turned off. " +
                                        "Start the patched card server first, or turn on \"Start card server\".");
                        Say($"Nothing is listening on {plan.Host}:{plan.Port}. Starting the selected programs anyway.");
                        break;
                    }

                    Enter(StartStage.StartingCardServer);
                    var server = StartProcess("Card server", plan.CardServerPath!, plan.CardServerArguments,
                        Path.GetDirectoryName(Path.GetFullPath(plan.CardServerPath!))!);

                    Enter(StartStage.WaitingForPort);
                    Say($"Waiting for {plan.Host}:{plan.Port} (up to {plan.PortTimeout.TotalSeconds:0} s)...");
                    int maxPolls = Math.Max(1, (int)Math.Ceiling(plan.PortTimeout / plan.PollInterval));
                    bool up = false;
                    for (int i = 0; i < maxPolls; i++)
                    {
                        if (await _probe.IsListeningAsync(plan.Host, plan.Port, ct).ConfigureAwait(false)) { up = true; break; }
                        if (server.HasExited)
                            return Fail($"The card server exited (code {server.ExitCode?.ToString() ?? "?"}) before it opened port {plan.Port}. " +
                                        "Check its window or its Logs folder.");
                        await _delay.WaitAsync(plan.PollInterval, ct).ConfigureAwait(false);
                    }
                    if (!up)
                        return Fail($"The card server did not open {plan.Host}:{plan.Port} within {plan.PortTimeout.TotalSeconds:0} s. " +
                                    "It is left running so you can read its window; check its Logs folder and server.json.");

                    var nowOwner = _owners.FindListener(plan.Port);
                    if (nowOwner is not null && PortOwnerClassifier.Classify(true, nowOwner, plan) == PortOwnerKind.StockServer)
                        return Fail($"Port {plan.Port} was taken by the stock card server {nowOwner.Describe()} instead of the patched one.");
                    Say($"Card server is listening on {plan.Host}:{plan.Port}.");
                    break;
            }

            if (plan.StartLm)
            {
                Enter(StartStage.StartingLm);
                StartProcess("LM", game.LmExe, "", game.Root);
                if (plan.StartClient && plan.LmToClientDelay > TimeSpan.Zero)
                {
                    Enter(StartStage.WaitingBeforeClient);
                    await _delay.WaitAsync(plan.LmToClientDelay, ct).ConfigureAwait(false);
                }
            }

            if (plan.StartClient)
            {
                Enter(StartStage.StartingClient);
                StartProcess("Client", game.ClientExe, "", game.Root);
            }

            Enter(StartStage.Running);
            Say("Started: " + (_started.Count == 0 ? "nothing new" : string.Join(", ", _started.Select(p => $"{p.Role} (pid {p.Id})"))) + ".");
            return new StartOutcome(StartStage.Running, "Running.", _started.ToList());
        }
        catch (OperationCanceledException)
        {
            Enter(StartStage.Cancelled);
            Say("Start cancelled. Programs that were already started keep running; use Stop to close them.");
            return new StartOutcome(StartStage.Cancelled, "Cancelled.", _started.ToList());
        }
        catch (Exception ex)
        {
            return Fail("Could not start: " + ex.Message);
        }
    }

    /// <summary>Kills the processes this launcher started, newest first. Returns how many were closed.</summary>
    public int Stop(bool includeCardServer)
    {
        int n = 0;
        for (int i = _started.Count - 1; i >= 0; i--)
        {
            var p = _started[i];
            if (!includeCardServer && p.Role == ComponentRoles.CardServer) continue;
            if (!p.HasExited) { p.Kill(); n++; Say($"Closed {p.Role} (pid {p.Id})."); }
            _started.RemoveAt(i);
        }
        Enter(StartStage.Idle);
        return n;
    }

    /// <summary>
    /// Stops one component of this profile: first the PID this launcher started, then any process whose exe
    /// path is in this profile's folder (never another cab's folder).
    /// </summary>
    public int StopRole(string role, StartPlan plan)
    {
        _started.RemoveAll(p => p.HasExited);
        var hits = ProfileProcessMatcher.Find(role, plan, _started, SafeList());
        int n = 0;
        var startedIds = _started.Where(p => p.Role == role).Select(p => p.Id).ToHashSet();
        foreach (var (pid, how) in hits)
        {
            var tracked = _started.FirstOrDefault(p => p.Id == pid);
            if (tracked is not null)
            {
                if (!tracked.HasExited) { tracked.Kill(); n++; Say($"Closed {role} (pid {pid}, {how})."); }
                _started.Remove(tracked);
            }
            else
            {
                _processes.KillTree(pid);
                n++;
                Say($"Closed {role} (pid {pid}, {how}).");
            }
        }
        if (n == 0) Say($"{role} is not running for this profile.");
        if (_started.All(p => p.HasExited)) Enter(StartStage.Idle);
        return n;
    }

    public ComponentStatus RoleStatus(string role, StartPlan plan)
    {
        _started.RemoveAll(p => p.HasExited);
        return ProfileProcessMatcher.Status(role, plan, _started, SafeList());
    }

    public IReadOnlyList<ComponentStatus> RoleStatuses(StartPlan plan) =>
        ComponentRoles.All.Select(r => RoleStatus(r, plan)).ToList();

    private IReadOnlyList<RunningProcessInfo> SafeList()
    {
        try { return _processes.List(); }
        catch (Exception ex)
        {
            Say("Could not list running programs: " + ex.Message);
            return Array.Empty<RunningProcessInfo>();
        }
    }

    public string? Validate(StartPlan plan)
    {
        if (string.IsNullOrWhiteSpace(plan.GameFolder)) return "Pick the game folder.";
        if (plan.Port is < 1 or > 65535) return "The card server port must be 1-65535.";
        if (plan.PollInterval <= TimeSpan.Zero) return "Poll interval must be positive.";
        if (!plan.StartCardServer && !plan.StartLm && !plan.StartClient) return "Nothing is selected to start.";

        var game = new GameFolder(plan.GameFolder);
        if (plan.StartCardServer)
        {
            if (string.IsNullOrWhiteSpace(plan.CardServerPath)) return "Pick the patched card server (Server-Infinite.exe or 3-Start-Server.bat).";
            if (!_fileExists(plan.CardServerPath)) return "Card server not found: " + plan.CardServerPath;
            var name = Path.GetFileName(plan.CardServerPath);
            if (string.Equals(name, "Server.exe", StringComparison.OrdinalIgnoreCase) ||
                PortOwnerClassifier.SamePath(plan.CardServerPath, game.StockServerExe))
                return "That is the pack's stock card server (Server.exe). Pick the patched Server-Infinite.exe or its 3-Start-Server.bat.";
            var ext = Path.GetExtension(plan.CardServerPath);
            if (!new[] { ".exe", ".bat", ".cmd" }.Contains(ext, StringComparer.OrdinalIgnoreCase))
                return "The card server must be an .exe, .bat or .cmd file.";
        }
        if (plan.StartLm && !_fileExists(game.LmExe)) return "LM not found: " + game.LmExe;
        if (plan.StartClient && !_fileExists(game.ClientExe)) return "Game client not found: " + game.ClientExe;
        return null;
    }

    private IStartedProcess StartProcess(string role, string file, string args, string workDir)
    {
        Say($"Starting {role}: {file}");
        var p = _starter.Start(new LaunchSpec(role, file, args, workDir));
        _started.Add(p);
        Say($"{role} started (pid {p.Id}).");
        return p;
    }

    private StartOutcome Fail(string message)
    {
        Say(message);
        Enter(StartStage.Failed);
        return new StartOutcome(StartStage.Failed, message, _started.ToList());
    }

    private void Enter(StartStage s)
    {
        Stage = s;
        StageChanged?.Invoke(s);
    }

    private void Say(string m) => Log?.Invoke(m);
}

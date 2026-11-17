using Exvs2Launcher.Core.Files;
using Exvs2Launcher.Core.Profiles;

namespace Exvs2Launcher.Core.Launch;

/// <summary>Status light for one component of one cab in the "All cabs" view.</summary>
public enum CabLight { Off, Waiting, Starting, Running, Failed, Skipped }

/// <summary>What "Start everything" needs from the per-cab launcher. Fakes implement this in tests.</summary>
public interface ICabControl
{
    /// <summary>Writes the cab's network settings (config.ini / lmnfig.ini). Only called while none of the cab's programs run.</summary>
    void Prepare(LauncherProfile profile);
    Task<StartOutcome> StartRoleAsync(LauncherProfile profile, string role, CancellationToken ct);
    ComponentStatus Status(LauncherProfile profile, string role);
    int Stop(LauncherProfile profile, string role);
}

/// <summary>The order "Start everything" will use, built from the included profiles.</summary>
public sealed class MultiCabPlan
{
    /// <summary>One profile per distinct card server (path + address); the others share it.</summary>
    public List<LauncherProfile> Servers { get; } = new();
    /// <summary>Card server key → every profile that uses that server.</summary>
    public Dictionary<string, List<LauncherProfile>> ServerUsers { get; } = new(StringComparer.OrdinalIgnoreCase);
    public List<LauncherProfile> Lms { get; } = new();
    public List<LauncherProfile> Clients { get; } = new();
    public List<LauncherProfile> Included { get; } = new();
    public List<string> Notes { get; } = new();

    public static string ServerKey(LauncherProfile p)
    {
        string path = p.CardServerPath ?? "";
        try { if (path.Length > 0) path = Path.GetFullPath(path.Trim().Trim('"')); } catch (Exception) { }
        return path.TrimEnd('\\', '/').ToUpperInvariant() + "|" + p.CardServerHost.Trim().ToLowerInvariant() + ":" + p.CardServerPort;
    }

    public static MultiCabPlan Build(IEnumerable<LauncherProfile> profiles)
    {
        var plan = new MultiCabPlan();
        foreach (var p in profiles)
        {
            if (!p.IncludeInStartEverything) { plan.Notes.Add($"{p.Name}: not included in Start everything."); continue; }
            if (string.IsNullOrWhiteSpace(p.GameFolder)) { plan.Notes.Add($"{p.Name}: no game folder; skipped."); continue; }
            plan.Included.Add(p);
            if (p.StartCardServer)
            {
                var key = ServerKey(p);
                if (!plan.ServerUsers.TryGetValue(key, out var users))
                {
                    users = new List<LauncherProfile>();
                    plan.ServerUsers[key] = users;
                    plan.Servers.Add(p);
                }
                users.Add(p);
            }
            if (p.StartLm) plan.Lms.Add(p);
            if (p.StartClient) plan.Clients.Add(p);
        }
        return plan;
    }
}

public sealed record MultiCabResult(bool Succeeded, bool Cancelled, string Message, IReadOnlyList<string> Problems);

/// <summary>
/// "Start everything": every included cab in the order a multi-cab PC needs: card servers (each distinct server once,
/// waiting for its port) → LM(s) → clients, one at a time with a stagger. It only starts programs; it never changes
/// CPU affinity or priority, never starts the stock <c>server\Server.exe</c> (the sequencer refuses it), and never
/// touches a program that is already running.
/// </summary>
public sealed class MultiCabOrchestrator
{
    private readonly ICabControl _cabs;
    private readonly IDelay _delay;
    private readonly Func<IReadOnlyList<string>> _blockers;

    /// <param name="blockers">Programs that must not run during a start (SnowLauncher). Game processes are checked per cab.</param>
    public MultiCabOrchestrator(ICabControl cabs, IDelay delay, Func<IReadOnlyList<string>>? blockers = null)
    {
        _cabs = cabs;
        _delay = delay;
        _blockers = blockers ?? (() => ProcessGuard.RunningConflicts()
            .Where(n => n.StartsWith("SnowLauncher", StringComparison.OrdinalIgnoreCase)).ToList());
    }

    public event Action<string>? Log;
    /// <summary>(profile name, role, light) whenever a light changes.</summary>
    public event Action<string, string, CabLight>? LightChanged;

    public bool IsRunning { get; private set; }

    public async Task<MultiCabResult> StartEverythingAsync(IEnumerable<LauncherProfile> profiles, LauncherSettings settings,
        CancellationToken ct = default)
    {
        if (IsRunning) return new MultiCabResult(false, false, "Start everything is already running.", Array.Empty<string>());
        IsRunning = true;
        var problems = new List<string>();
        try
        {
            var plan = MultiCabPlan.Build(profiles);
            foreach (var n in plan.Notes) Say(n);
            if (plan.Included.Count == 0)
                return Done(false, "No cab profiles are included in Start everything.", problems);

            var blockers = _blockers();
            if (blockers.Count > 0)
                return Done(false, "Close these first: " + string.Join(", ", blockers) + ". SnowLauncher rewrites the game files and starts the stock server.", problems);

            foreach (var p in plan.Included)
                foreach (var role in ComponentRoles.All)
                    Light(p, role, Wants(p, role) ? CabLight.Waiting : CabLight.Off);

            // 1. Files: only for cabs that are fully stopped, so a running game never has its files rewritten.
            var blocked = new HashSet<LauncherProfile>();
            foreach (var p in plan.Included)
            {
                bool busy = _cabs.Status(p, ComponentRoles.Lm).Running || _cabs.Status(p, ComponentRoles.Client).Running;
                if (busy) { Say($"{p.Name}: LM or client already running; its files are left as they are."); continue; }
                try { _cabs.Prepare(p); }
                catch (Exception ex)
                {
                    problems.Add($"{p.Name}: could not write its settings ({ex.Message}).");
                    blocked.Add(p);
                    Light(p, ComponentRoles.Lm, CabLight.Skipped);
                    Light(p, ComponentRoles.Client, CabLight.Skipped);
                }
            }

            // 2. Card servers, each distinct one once.
            foreach (var rep in plan.Servers)
            {
                ct.ThrowIfCancellationRequested();
                var users = plan.ServerUsers[MultiCabPlan.ServerKey(rep)];
                foreach (var u in users) Light(u, ComponentRoles.CardServer, CabLight.Starting);
                var outcome = await StartRole(rep, ComponentRoles.CardServer, ct).ConfigureAwait(false);
                if (outcome.FinalStage == StartStage.Cancelled) throw new OperationCanceledException();
                var light = outcome.Succeeded ? CabLight.Running : CabLight.Failed;
                foreach (var u in users)
                {
                    Light(u, ComponentRoles.CardServer, light);
                    if (!outcome.Succeeded && blocked.Add(u))
                    {
                        Light(u, ComponentRoles.Lm, Wants(u, ComponentRoles.Lm) ? CabLight.Skipped : CabLight.Off);
                        Light(u, ComponentRoles.Client, Wants(u, ComponentRoles.Client) ? CabLight.Skipped : CabLight.Off);
                    }
                }
                if (!outcome.Succeeded)
                    problems.Add($"Card server for {string.Join(", ", users.Select(x => x.Name))}: {outcome.Message} Their LM / client were skipped.");
            }

            // 3. LM(s).
            bool lmStarted = false;
            foreach (var p in plan.Lms)
            {
                ct.ThrowIfCancellationRequested();
                if (blocked.Contains(p)) continue;
                bool wasRunning = _cabs.Status(p, ComponentRoles.Lm).Running;
                Light(p, ComponentRoles.Lm, CabLight.Starting);
                var outcome = await StartRole(p, ComponentRoles.Lm, ct).ConfigureAwait(false);
                if (outcome.FinalStage == StartStage.Cancelled) throw new OperationCanceledException();
                Light(p, ComponentRoles.Lm, outcome.Succeeded ? CabLight.Running : CabLight.Failed);
                if (!outcome.Succeeded) problems.Add($"{p.Name} LM: {outcome.Message}");
                else if (!wasRunning) lmStarted = true;
            }

            // 4. Clients, staggered.
            var clients = plan.Clients.Where(p => !blocked.Contains(p)).ToList();
            bool anyClientStarted = false;
            if (lmStarted && clients.Count > 0 && settings.LmToClientsDelaySeconds > 0)
            {
                Say($"Waiting {settings.LmToClientsDelaySeconds} s after the LM before the first client...");
                await _delay.WaitAsync(TimeSpan.FromSeconds(settings.LmToClientsDelaySeconds), ct).ConfigureAwait(false);
            }
            foreach (var p in clients)
            {
                ct.ThrowIfCancellationRequested();
                bool wasRunning = _cabs.Status(p, ComponentRoles.Client).Running;
                if (anyClientStarted && !wasRunning && settings.ClientStaggerSeconds > 0)
                {
                    Say($"Waiting {settings.ClientStaggerSeconds} s before {p.Name}...");
                    await _delay.WaitAsync(TimeSpan.FromSeconds(settings.ClientStaggerSeconds), ct).ConfigureAwait(false);
                }
                Light(p, ComponentRoles.Client, CabLight.Starting);
                var outcome = await StartRole(p, ComponentRoles.Client, ct).ConfigureAwait(false);
                if (outcome.FinalStage == StartStage.Cancelled) throw new OperationCanceledException();
                Light(p, ComponentRoles.Client, outcome.Succeeded ? CabLight.Running : CabLight.Failed);
                if (!outcome.Succeeded) problems.Add($"{p.Name} client: {outcome.Message}");
                else if (!wasRunning) anyClientStarted = true;
            }

            return problems.Count == 0
                ? Done(true, $"Start everything finished: {plan.Included.Count} cab(s).", problems)
                : Done(false, $"Start everything finished with {problems.Count} problem(s).", problems);
        }
        catch (OperationCanceledException)
        {
            Say("Start everything cancelled. Programs already started keep running; use Stop everything to close them.");
            return new MultiCabResult(false, true, "Cancelled.", problems);
        }
        finally { IsRunning = false; }
    }

    /// <summary>Stops every included cab: clients first, then LM(s), then card servers. Returns how many programs were closed.</summary>
    public int StopEverything(IEnumerable<LauncherProfile> profiles)
    {
        var plan = MultiCabPlan.Build(profiles);
        int n = 0;
        foreach (var role in new[] { ComponentRoles.Client, ComponentRoles.Lm, ComponentRoles.CardServer })
        {
            var list = role switch
            {
                ComponentRoles.Client => plan.Included,
                ComponentRoles.Lm => plan.Included,
                _ => plan.Servers,
            };
            foreach (var p in list)
            {
                try { n += _cabs.Stop(p, role); }
                catch (Exception ex) { Say($"{p.Name} {role}: could not stop ({ex.Message})."); }
                if (role == ComponentRoles.CardServer)
                    foreach (var u in plan.ServerUsers[MultiCabPlan.ServerKey(p)]) Light(u, role, CabLight.Off);
                else Light(p, role, CabLight.Off);
            }
        }
        Say(n == 0 ? "Nothing was running." : $"Stop everything closed {n} program(s).");
        return n;
    }

    /// <summary>Current light for one component (Running / Off) from the live process list.</summary>
    public CabLight Observe(LauncherProfile p, string role) =>
        _cabs.Status(p, role).Running ? CabLight.Running : CabLight.Off;

    private async Task<StartOutcome> StartRole(LauncherProfile p, string role, CancellationToken ct)
    {
        Say($"--- {p.Name}: {role} ---");
        try { return await _cabs.StartRoleAsync(p, role, ct).ConfigureAwait(false); }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { return new StartOutcome(StartStage.Failed, ex.Message, Array.Empty<IStartedProcess>()); }
    }

    private static bool Wants(LauncherProfile p, string role) => role switch
    {
        ComponentRoles.CardServer => p.StartCardServer,
        ComponentRoles.Lm => p.StartLm,
        _ => p.StartClient,
    };

    private MultiCabResult Done(bool ok, string message, List<string> problems)
    {
        Say(message);
        foreach (var pr in problems) Say("  " + pr);
        return new MultiCabResult(ok, false, message, problems);
    }

    private void Light(LauncherProfile p, string role, CabLight light) => LightChanged?.Invoke(p.Name, role, light);
    private void Say(string m) => Log?.Invoke(m);
}

/// <summary>
/// The real <see cref="ICabControl"/>: one <see cref="CabLauncher"/> (and so one PID-tracking sequencer) per profile.
/// </summary>
public sealed class SequencerCabControl : ICabControl
{
    private readonly Func<ProfileFile> _all;
    private readonly Func<StartSequencer> _newSequencer;
    private readonly GameConfigWriter _writer;
    private readonly Dictionary<LauncherProfile, CabLauncher> _cabs = new(ReferenceEqualityComparer.Instance);

    public SequencerCabControl(Func<ProfileFile> all, Func<StartSequencer> newSequencer, GameConfigWriter writer)
    {
        _all = all;
        _newSequencer = newSequencer;
        _writer = writer;
    }

    public event Action<string>? Log;

    private CabLauncher For(LauncherProfile p)
    {
        if (!_cabs.TryGetValue(p, out var c))
        {
            var seq = _newSequencer();
            seq.Log += m => Log?.Invoke(m);
            c = new CabLauncher(seq, _writer);
            c.Log += m => Log?.Invoke(m);
            _cabs[p] = c;
        }
        return c;
    }

    public void Prepare(LauncherProfile profile) => For(profile).Prepare(_all(), profile);

    public Task<StartOutcome> StartRoleAsync(LauncherProfile profile, string role, CancellationToken ct) =>
        For(profile).StartRoleAsync(profile, role, ct);

    public ComponentStatus Status(LauncherProfile profile, string role) =>
        For(profile).Sequencer.RoleStatus(role, StatusPlan(profile));

    public int Stop(LauncherProfile profile, string role) =>
        For(profile).Sequencer.StopRole(role, StatusPlan(profile));

    /// <summary>A plan that covers all three roles of a profile, for status checks and stops.</summary>
    public static StartPlan StatusPlan(LauncherProfile p)
    {
        var b = p.ToStartPlan();
        return new StartPlan
        {
            GameFolder = b.GameFolder, CardServerPath = b.CardServerPath, CardServerArguments = b.CardServerArguments,
            StartCardServer = true, StartLm = true, StartClient = true, Host = b.Host, Port = b.Port,
        };
    }
}

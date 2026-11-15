using Exvs2Launcher.Core.Files;
using Exvs2Launcher.Core.Net;
using Exvs2Launcher.Core.Profiles;

namespace Exvs2Launcher.Core.Launch;

/// <summary>
/// What the Start button does: refuse while SnowLauncher or the game is open, optionally auto-detect the PCB
/// (client) LAN IP — never the LM IP — write the card-server address, warn about a duplicate serial, and note
/// when PCB and LM share an address (fine for a single cab; use a separate LM IP for multiple cabs), then run
/// the <see cref="StartSequencer"/>.
/// </summary>
public sealed class CabLauncher
{
    private readonly StartSequencer _sequencer;
    private readonly GameConfigWriter _writer;
    private readonly Func<IReadOnlyList<string>> _conflicts;
    private readonly Func<IReadOnlyList<NicAddress>> _nics;

    public CabLauncher(StartSequencer sequencer, GameConfigWriter writer,
        Func<IReadOnlyList<string>>? conflicts = null, Func<IReadOnlyList<NicAddress>>? nics = null)
    {
        _sequencer = sequencer;
        _writer = writer;
        _conflicts = conflicts ?? (() => ProcessGuard.RunningConflicts());
        _nics = nics ?? LanAddressDetector.Enumerate;
    }

    public StartSequencer Sequencer => _sequencer;
    public event Action<string>? Log;

    public async Task<StartOutcome> StartAsync(ProfileFile all, LauncherProfile profile, CancellationToken ct = default)
    {
        var running = _conflicts();
        if (running.Count > 0)
            return new StartOutcome(StartStage.Failed,
                "Close these first: " + string.Join(", ", running) +
                ". SnowLauncher rewrites the game files and starts the stock server; a running game would be started twice.",
                Array.Empty<IStartedProcess>());

        var game = new GameFolder(profile.GameFolder);
        if (File.Exists(game.ClientIni))
        {
            var state = _writer.Read(game.Root);
            var dupes = SerialRegistry.Duplicates(all, profile, state.Serial, _writer);
            if (dupes.Count > 0)
                Say($"Warning: serial {state.Serial} is also used by {string.Join(", ", dupes)}. The matchmaker treats them as the same cab. Use New serial.");
            if (!state.CardValid)
                Say("Warning: card.ini is missing or not in the 20-digit / 32-hex format (accessCode / AccessCode and chipId / ChipId are accepted). Use New card only if this cab has no working card.");

            var nics = _nics();
            var decision = NetworkBinding.Resolve(
                state.ClientIp, state.LmIp, profile.PcbIp, profile.LmIp, profile.AutoDetectIp, nics);
            foreach (var m in decision.Messages) Say(m);
            profile.PcbIp = decision.PcbIp;
            profile.LmIp = decision.LmIp;

            var serverInfo = CardServerInspector.Inspect(profile.CardServerPath);
            if (serverInfo.Summary is not null)
                Say("Card server: " + serverInfo.Summary + (serverInfo.Source is not null ? " (from " + serverInfo.Source + ")" : ""));
            if (serverInfo.Warning is not null)
                Say("Warning: " + serverInfo.Warning);

            var report = _writer.ApplyNetwork(game.Root, decision.PcbIp, decision.LmIp, profile.ServerAddress, profile.SyncSnowLauncherIni);
            foreach (var w in report.Written) Say("Updated " + Path.GetFileName(w));
            foreach (var b in report.Backups) Say("Backup: " + Path.GetFileName(b));
            foreach (var n in report.Notes) Say(n);
        }
        else
        {
            Say("config.ini not found in the game folder; nothing written.");
        }

        return await _sequencer.RunAsync(profile.ToStartPlan(), ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Starts a single component (card server, LM, or client) without rewriting game files or refusing
    /// just because the other components are already running.
    /// </summary>
    public Task<StartOutcome> StartRoleAsync(LauncherProfile profile, string role, CancellationToken ct = default)
    {
        var plan = PlanForRole(profile, role);
        return _sequencer.RunAsync(plan, ct);
    }

    public static StartPlan PlanForRole(LauncherProfile profile, string role)
    {
        var full = profile.ToStartPlan();
        bool server = role == ComponentRoles.CardServer;
        bool lm = role == ComponentRoles.Lm;
        bool client = role == ComponentRoles.Client;
        if (!server && !lm && !client)
            throw new ArgumentException("Unknown role: " + role, nameof(role));
        return new StartPlan
        {
            GameFolder = full.GameFolder,
            CardServerPath = full.CardServerPath,
            CardServerArguments = full.CardServerArguments,
            StartCardServer = server,
            StartLm = lm,
            StartClient = client,
            Host = full.Host,
            Port = full.Port,
            PortTimeout = full.PortTimeout,
            PollInterval = full.PollInterval,
            LmToClientDelay = TimeSpan.Zero,
            ReuseRunningPatchedServer = true,
            RequireCardServerPort = server,
        };
    }

    private void Say(string m) => Log?.Invoke(m);
}


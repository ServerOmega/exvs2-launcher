using Exvs2Launcher.Core.Files;
using Exvs2Launcher.Core.Net;
using Exvs2Launcher.Core.Profiles;
using Exvs2Launcher.Core.VirtualLan;

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
    private readonly VirtualLanController? _vlan;
    private readonly Func<LauncherProfile, string?> _vlanKey;

    public CabLauncher(StartSequencer sequencer, GameConfigWriter writer,
        Func<IReadOnlyList<string>>? conflicts = null, Func<IReadOnlyList<NicAddress>>? nics = null,
        VirtualLanController? virtualLan = null, Func<LauncherProfile, string?>? virtualLanKey = null)
    {
        _sequencer = sequencer;
        _writer = writer;
        _conflicts = conflicts ?? (() => ProcessGuard.RunningConflicts());
        _nics = nics ?? LanAddressDetector.Enumerate;
        _vlan = virtualLan;
        _vlanKey = virtualLanKey ?? (_ => null);
    }

    public StartSequencer Sequencer => _sequencer;
    public VirtualLanController? VirtualLan => _vlan;

    /// <summary>
    /// Virtual LAN mode only: starts edge (or reuses it) and returns the TAP IPv4, or a failed outcome.
    /// Internet / LAN mode returns (null, null) and never touches virtual adapters.
    /// </summary>
    private async Task<(string? Ip, StartOutcome? Failure)> EnsureVirtualLanAsync(LauncherProfile profile, CancellationToken ct)
    {
        if (!profile.VirtualLan.Enabled) return (null, null);
        if (_vlan is null)
            return (null, new StartOutcome(StartStage.Failed, "Virtual LAN mode is on, but this build has no n2n support wired in.", Array.Empty<IStartedProcess>()));
        var r = await _vlan.StartAsync(profile.VirtualLan, _vlanKey(profile), ct).ConfigureAwait(false);
        if (!r.Succeeded || r.Ip is null)
            return (null, new StartOutcome(r.Message == "Cancelled." ? StartStage.Cancelled : StartStage.Failed,
                "Virtual LAN: " + r.Message, Array.Empty<IStartedProcess>()));
        profile.VirtualLan.LastVirtualIp = r.Ip;
        return (r.Ip, null);
    }
    public event Action<string>? Log;

    public async Task<StartOutcome> StartAsync(ProfileFile all, LauncherProfile profile, CancellationToken ct = default)
    {
        var running = _conflicts();
        if (running.Count > 0)
            return new StartOutcome(StartStage.Failed,
                "Close these first: " + string.Join(", ", running) +
                ". SnowLauncher rewrites the game files and starts the stock server; a running game would be started twice.",
                Array.Empty<IStartedProcess>());

        var (vlanIp, vlanFailure) = await EnsureVirtualLanAsync(profile, ct).ConfigureAwait(false);
        if (vlanFailure is not null) return vlanFailure;

        var game = new GameFolder(profile.GameFolder);
        if (File.Exists(game.ClientIni))
        {
            var state = _writer.Read(game.Root);
            var dupes = SerialRegistry.Duplicates(all, profile, state.Serial, _writer);
            if (dupes.Count > 0)
                Say($"Warning: serial {state.Serial} is also used by {string.Join(", ", dupes)}. The matchmaker treats them as the same cab. Use New serial.");
            if (!state.CardValid)
                Say("Warning: card.ini is missing or not in the 20-digit / 32-hex format (accessCode / AccessCode and chipId / ChipId are accepted). Use New card only if this cab has no working card.");

            NetworkDecision decision;
            if (vlanIp is not null)
            {
                // Mode A: the whole cab lives on the TAP. The profile's LAN IPs are left untouched so turning
                // virtual LAN off goes back to them.
                decision = new NetworkDecision(vlanIp, vlanIp, new[]
                {
                    $"Virtual LAN mode: config.ini and lmnfig.ini IpAddress → {vlanIp} (TAP). Card server stays at {profile.ServerAddress}.",
                }, true, false);
            }
            else
            {
                string? clientNow = state.ClientIp, lmNow = state.LmIp;
                var lastVirtual = profile.VirtualLan.LastVirtualIp;
                if (lastVirtual is not null)
                {
                    if (NetworkBinding.SameIp(clientNow, lastVirtual))
                    {
                        Say($"config.ini IpAddress {clientNow} is the old virtual LAN address; virtual LAN is off, so it is replaced.");
                        clientNow = null;
                    }
                    if (NetworkBinding.SameIp(lmNow, lastVirtual))
                    {
                        Say($"lmnfig.ini IpAddress {lmNow} is the old virtual LAN address; virtual LAN is off, so the profile LM IP is used.");
                        lmNow = null;
                    }
                }
                var nics = _nics();
                decision = NetworkBinding.Resolve(
                    clientNow, lmNow, profile.PcbIp, profile.LmIp, profile.AutoDetectIp, nics);
                profile.PcbIp = decision.PcbIp;
                profile.LmIp = decision.LmIp;
                if (lastVirtual is not null && decision.PcbIp is not null && decision.LmIp is not null)
                    profile.VirtualLan.LastVirtualIp = null;
            }
            foreach (var m in decision.Messages) Say(m);

            var serverInfo = CardServerInspector.Inspect(profile.CardServerPath);
            if (serverInfo.Summary is not null)
                Say("Card server: " + serverInfo.Summary + (serverInfo.Source is not null ? " (from " + serverInfo.Source + ")" : ""));
            if (serverInfo.Warning is not null)
                Say("Warning: " + serverInfo.Warning);

            var expectedServer = profile.ServerAddress;
            foreach (var problem in CardServerEndpoint.Inspect(state.ClientServer, state.LmServer, expectedServer))
                Say("Warning: " + problem);
            Say(SocketHookSetting.Display(state.LmDisableSocketHook));
            var hookWarn = SocketHookSetting.WarnIfDangerous(
                state.LmDisableSocketHook, decision.PcbIp, decision.LmIp,
                multipleCabs: all.Profiles.Count(p => !string.IsNullOrWhiteSpace(p.GameFolder)) > 1,
                snowHook: state.SnowDisableSocketHook);
            if (hookWarn is not null)
                Say("Warning: " + hookWarn);
            else if (vlanIp is not null && SocketHookSetting.IsDisabled(state.LmDisableSocketHook))
                Say("Warning: lmnfig.ini DisableSocketHook=True. In virtual LAN mode the LM must stay on the TAP address; with the hook off " +
                    SocketHookSetting.GrabEveryAddress + ". Set DisableSocketHook=False.");

            var report = _writer.ApplyNetwork(game.Root, decision.PcbIp, decision.LmIp, expectedServer, profile.SyncSnowLauncherIni);
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
    public async Task<StartOutcome> StartRoleAsync(LauncherProfile profile, string role, CancellationToken ct = default)
    {
        var plan = PlanForRole(profile, role);
        if (profile.VirtualLan.Enabled && role is ComponentRoles.Lm or ComponentRoles.Client
            && !_sequencer.RoleStatus(role, plan).Running)
        {
            var (ip, failure) = await EnsureVirtualLanAsync(profile, ct).ConfigureAwait(false);
            if (failure is not null) return failure;
            var game = new GameFolder(profile.GameFolder);
            if (ip is not null && File.Exists(role == ComponentRoles.Lm ? game.LmIni : game.ClientIni))
            {
                var report = role == ComponentRoles.Lm
                    ? _writer.ApplyNetwork(game.Root, null, ip, null, profile.SyncSnowLauncherIni)
                    : _writer.ApplyNetwork(game.Root, ip, null, null, profile.SyncSnowLauncherIni);
                Say($"Virtual LAN mode: {(role == ComponentRoles.Lm ? "lmnfig.ini" : "config.ini")} IpAddress → {ip} (TAP).");
                foreach (var w in report.Written) Say("Updated " + Path.GetFileName(w));
                foreach (var b in report.Backups) Say("Backup: " + Path.GetFileName(b));
            }
        }
        return await _sequencer.RunAsync(plan, ct).ConfigureAwait(false);
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


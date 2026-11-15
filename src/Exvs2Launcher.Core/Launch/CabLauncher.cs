using Exvs2Launcher.Core.Files;
using Exvs2Launcher.Core.Net;
using Exvs2Launcher.Core.Profiles;

namespace Exvs2Launcher.Core.Launch;

/// <summary>
/// What the Start button does: refuse while SnowLauncher or the game is open, write this PC's LAN IP and the
/// card-server address into the game files (if auto-detect is on), warn about a duplicate serial, then run the
/// <see cref="StartSequencer"/>.
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
                Say("Warning: card.ini is missing or not in the 20-digit / 32-hex format. Use New card.");

            if (profile.AutoDetectIp)
            {
                var best = LanAddressDetector.PickBest(_nics());
                if (best is null) Say("No network adapter found for auto-detect; IPs left as they are.");
                else
                {
                    profile.PcbIp = best.Address;
                    profile.LmIp = best.Address;
                    Say($"This PC's LAN IP: {best.Display}");
                }
            }
            var report = _writer.ApplyNetwork(game.Root, profile.PcbIp, profile.LmIp, profile.ServerAddress, profile.SyncSnowLauncherIni);
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

    private void Say(string m) => Log?.Invoke(m);
}

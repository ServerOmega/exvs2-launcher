using System.Text;
using Exvs2Launcher.Core.Diagnostics;
using Exvs2Launcher.Core.Launch;
using Exvs2Launcher.Core.Profiles;

namespace Exvs2Launcher.Core.Health;

public sealed record HealthEvent(DateTime At, string Cab, string Source, HealthEventKind Kind, string Message)
{
    public string? SavedPath { get; init; }
    public bool Important => LogClassifier.IsImportant(Kind);
    public string KindLabel => Kind switch
    {
        HealthEventKind.StoppedUnexpectedly => "Stopped unexpectedly",
        HealthEventKind.PossibleDisconnect => "Possible disconnect",
        HealthEventKind.FailedBattle => "Failed battle",
        HealthEventKind.MatchFound => "Match found",
        HealthEventKind.RoomEnded => "Room ended",
        HealthEventKind.ResultSaved => "Result saved",
        HealthEventKind.QueueActivity => "Queue",
        _ => Kind.ToString(),
    };
}

/// <summary>
/// Read-only health watch for every profile: compares folder-scoped PIDs between polls to report starts and exits,
/// and tails each distinct card server's log for errors, matches and drops. It never opens, signals, suspends or
/// re-prioritises a running program. Important events are written (masked) to <c>health-events\*.txt</c> with the
/// surrounding log lines.
/// </summary>
public sealed class HealthMonitor
{
    private readonly Func<LauncherProfile, string, ComponentStatus> _status;
    private readonly Func<string, LogTailer?> _tailerFor;
    private readonly Func<DateTime> _now;
    private readonly string? _saveDir;
    private readonly Dictionary<(string Cab, string Role), int?> _lastPid = new();
    private readonly HashSet<(string Cab, string Role)> _expectedStops = new();
    private readonly Dictionary<string, LogTailer?> _tailers = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, DateTime> _lastSame = new(StringComparer.Ordinal);

    /// <param name="status">Folder-scoped status of one role of one profile.</param>
    /// <param name="tailerFor">Card server path → log tailer (null if it has no Logs folder).</param>
    /// <param name="saveDir">Where important events are saved; null = do not save.</param>
    public HealthMonitor(Func<LauncherProfile, string, ComponentStatus> status, Func<string, LogTailer?>? tailerFor = null,
        string? saveDir = null, Func<DateTime>? now = null)
    {
        _status = status;
        _tailerFor = tailerFor ?? (path => CardServerFiles.LogsDir(path) is string d ? LogTailer.ForFolder(d) : null);
        _saveDir = saveDir;
        _now = now ?? (() => DateTime.Now);
    }

    public int ContextLines { get; set; } = 20;

    /// <summary>Call before the launcher stops a program on purpose so the exit is not reported as unexpected.</summary>
    public void ExpectStop(string cab, string role) => _expectedStops.Add((cab, role));

    public IReadOnlyList<HealthEvent> Poll(IEnumerable<LauncherProfile> profiles)
    {
        var events = new List<HealthEvent>();
        var list = profiles.Where(p => !string.IsNullOrWhiteSpace(p.GameFolder) || !string.IsNullOrWhiteSpace(p.CardServerPath)).ToList();

        // Processes
        foreach (var p in list)
        {
            foreach (var role in ComponentRoles.All)
            {
                ComponentStatus st;
                try { st = _status(p, role); }
                catch (Exception) { continue; }
                var key = (p.Name, role);
                bool known = _lastPid.TryGetValue(key, out var before);
                int? now = st.Running ? st.Pid : null;
                _lastPid[key] = now;
                if (!known) continue; // first look: just remember
                if (before is null && now is not null)
                {
                    _expectedStops.Remove(key);
                    events.Add(new HealthEvent(_now(), p.Name, role, HealthEventKind.Started, $"{role} started (pid {now})."));
                }
                else if (before is not null && now != before)
                {
                    bool expected = _expectedStops.Remove(key);
                    var kind = expected ? HealthEventKind.Exited : HealthEventKind.StoppedUnexpectedly;
                    var msg = expected
                        ? $"{role} closed by the launcher (pid {before})."
                        : $"{role} exited without the launcher stopping it (pid {before}). It may have crashed or been closed by hand.";
                    if (now is not null) msg += $" Now running as pid {now}.";
                    var ev = new HealthEvent(_now(), p.Name, role, kind, msg);
                    if (ev.Important) ev = Save(ev, TailerFor(p)?.Recent(ContextLines) ?? Array.Empty<string>(), null);
                    events.Add(ev);
                }
            }
        }

        // Card server logs (each distinct server once)
        var byServer = list.Where(p => !string.IsNullOrWhiteSpace(p.CardServerPath))
            .GroupBy(p => ServerKey(p.CardServerPath!), StringComparer.OrdinalIgnoreCase);
        foreach (var group in byServer)
        {
            var tailer = TailerFor(group.First());
            if (tailer is null) continue;
            IReadOnlyList<string> lines;
            try { lines = tailer.Poll(); }
            catch (Exception) { continue; }
            if (lines.Count == 0) continue;
            var users = group.ToList();
            var context = new List<string>(tailer.Recent(lines.Count + ContextLines));
            int offset = context.Count - lines.Count;
            for (int i = 0; i < lines.Count; i++)
            {
                var finding = LogClassifier.Classify(lines[i]);
                if (finding is null) continue;
                var cab = Attribute(lines[i], users);
                var text = Redactor.MaskText(finding.Summary);
                var dedupeKey = finding.Kind + "|" + cab + "|" + text;
                if (finding.Kind == HealthEventKind.Warning && _lastSame.TryGetValue(dedupeKey, out var last) &&
                    _now() - last < TimeSpan.FromSeconds(60)) continue;
                _lastSame[dedupeKey] = _now();
                var ev = new HealthEvent(_now(), cab, "Card server log", finding.Kind, text);
                if (ev.Important)
                {
                    int idx = offset + i;
                    var before = context.Skip(Math.Max(0, idx - ContextLines)).Take(Math.Min(ContextLines, Math.Max(0, idx))).ToList();
                    ev = Save(ev, before, lines[i]);
                }
                events.Add(ev);
            }
        }
        if (_lastSame.Count > 2000) _lastSame.Clear();
        return events;
    }

    /// <summary>A log line belongs to a cab when it mentions that cab's serial; otherwise to every cab on that server.</summary>
    public static string Attribute(string line, IReadOnlyList<LauncherProfile> users)
    {
        foreach (var p in users)
        {
            if (string.IsNullOrWhiteSpace(p.Serial) || p.Serial!.Trim().Length < 4) continue;
            var s = p.Serial.Trim();
            // Server lines name a cab as "serial=XXXX" (ROOM-END, QUARANTINE) or "XXXX@loc" (NETREPORT pcbs=[...]).
            if (line.Contains("serial=" + s, StringComparison.OrdinalIgnoreCase) ||
                line.Contains(s + "@", StringComparison.OrdinalIgnoreCase))
                return p.Name;
        }
        return users.Count == 1 ? users[0].Name : "Card server (" + string.Join(", ", users.Select(u => u.Name)) + ")";
    }

    private LogTailer? TailerFor(LauncherProfile p)
    {
        if (string.IsNullOrWhiteSpace(p.CardServerPath)) return null;
        var key = ServerKey(p.CardServerPath);
        if (!_tailers.TryGetValue(key, out var t))
        {
            try { t = _tailerFor(p.CardServerPath); } catch (Exception) { t = null; }
            _tailers[key] = t;
        }
        return t;
    }

    private static string ServerKey(string path)
    {
        try { return Path.GetFullPath(path.Trim().Trim('"')); } catch (Exception) { return path; }
    }

    private HealthEvent Save(HealthEvent ev, IReadOnlyList<string> before, string? line)
    {
        if (_saveDir is null) return ev;
        try
        {
            Directory.CreateDirectory(_saveDir);
            var name = $"{ev.At:yyyyMMdd-HHmmss}-{ev.Kind}-{DiagnosticsBundleBuilder.SafeName(ev.Cab)}.txt";
            var path = Path.Combine(_saveDir, name);
            for (int n = 2; File.Exists(path); n++) path = Path.Combine(_saveDir, name.Replace(".txt", $"-{n}.txt"));
            var sb = new StringBuilder();
            sb.AppendLine($"Time:   {ev.At:yyyy-MM-dd HH:mm:ss} (local)");
            sb.AppendLine($"Cab:    {ev.Cab}");
            sb.AppendLine($"Source: {ev.Source}");
            sb.AppendLine($"Event:  {ev.KindLabel}");
            sb.AppendLine($"Detail: {ev.Message}");
            sb.AppendLine();
            sb.AppendLine($"--- card server log, {before.Count} line(s) before (masked) ---");
            foreach (var l in before) sb.AppendLine(Redactor.MaskText(l));
            if (line is not null)
            {
                sb.AppendLine("--- the line ---");
                sb.AppendLine(Redactor.MaskText(line));
            }
            File.WriteAllText(path, sb.ToString());
            PruneSaved();
            return ev with { SavedPath = path };
        }
        catch (Exception) { return ev; }
    }

    private void PruneSaved()
    {
        try
        {
            var old = new DirectoryInfo(_saveDir!).GetFiles("*.txt").OrderByDescending(f => f.LastWriteTimeUtc).Skip(500);
            foreach (var f in old) f.Delete();
        }
        catch (Exception) { }
    }
}

using System.Text.RegularExpressions;

namespace Exvs2Launcher.Core.Health;

public enum HealthEventKind
{
    Started, Exited, StoppedUnexpectedly,
    Error, Warning, MatchFound, RoomEnded, PossibleDisconnect, FailedBattle, ResultSaved, QueueActivity,
}

/// <summary>What one card server (or matchmaker) log line means for the Health tab. Null = not interesting.</summary>
public sealed record LogFinding(HealthEventKind Kind, string Summary);

/// <summary>
/// Classifies Serilog lines written by the patched card server / matchmaker (<c>2026-11-14 09:58:27.685 -08:00 [INF] ...</c>).
/// The rules only look at text the server already logs: levels, MATCH / ROOM-END / NETREPORT / SaveVsmOnResult /
/// SaveFailedBattleLogOn lines, and 21-xx error codes if a line mentions one. Game-side 21-01 errors that never reach
/// the server cannot be seen here.
/// </summary>
public static partial class LogClassifier
{
    [GeneratedRegex(@"^(?<ts>\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}(?:\.\d+)?(?: [+-]\d{2}:\d{2})?) \[(?<lvl>[A-Z]{3})\] (?<msg>.*)$")]
    private static partial Regex Serilog();

    [GeneratedRegex(@"\b21-\d{2}\b")]
    private static partial Regex ErrorCode21();

    [GeneratedRegex(@"\breason=(?<r>[A-Za-z-]+)")]
    private static partial Regex Reason();

    public static (DateTimeOffset? At, string Level, string Message) Parse(string line)
    {
        var m = Serilog().Match(line);
        if (!m.Success) return (null, "", line);
        DateTimeOffset? at = DateTimeOffset.TryParse(m.Groups["ts"].Value, System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.AssumeLocal, out var t) ? t : null;
        return (at, m.Groups["lvl"].Value, m.Groups["msg"].Value);
    }

    public static LogFinding? Classify(string line)
    {
        if (string.IsNullOrWhiteSpace(line)) return null;
        var (_, level, msg) = Parse(line);
        if (level.Length == 0 && line.StartsWith(' ')) return null; // continuation of a multi-line entry

        var code = ErrorCode21().Match(msg);
        if (code.Success) return new LogFinding(HealthEventKind.PossibleDisconnect, $"Error {code.Value} mentioned: " + Short(msg));

        if (msg.StartsWith("ROOM-END ", StringComparison.Ordinal))
        {
            var r = Reason().Match(msg);
            var reason = r.Success ? r.Groups["r"].Value : "";
            if (reason is "cancel-after-match" or "timeout")
                return new LogFinding(HealthEventKind.PossibleDisconnect, "Room ended early (" + reason + "): " + Short(msg));
            return new LogFinding(HealthEventKind.RoomEnded, Short(msg));
        }
        if (msg.StartsWith("MATCH room=", StringComparison.Ordinal))
            return msg.Contains("cpu-stand-ins", StringComparison.Ordinal) ? null : new LogFinding(HealthEventKind.MatchFound, Short(msg));
        if (msg.Contains("SaveFailedBattleLogOn", StringComparison.Ordinal))
            return new LogFinding(HealthEventKind.FailedBattle, "The game reported a failed (dropped) battle: " + Short(msg));
        if (msg.StartsWith("SaveVsmOnResult stored", StringComparison.Ordinal))
            return new LogFinding(HealthEventKind.ResultSaved, Short(msg));
        if (msg.StartsWith("PAIR-SEARCH start", StringComparison.Ordinal) || msg.Contains("EntryMatching", StringComparison.Ordinal))
            return new LogFinding(HealthEventKind.QueueActivity, Short(msg));

        if (level is "ERR" or "FTL") return new LogFinding(HealthEventKind.Error, Short(msg));
        if (level == "WRN" || msg.StartsWith("NET-WARN", StringComparison.Ordinal) || msg.StartsWith("SEC-REFUSE", StringComparison.Ordinal))
            return new LogFinding(HealthEventKind.Warning, Short(msg));
        return null;
    }

    public static bool IsImportant(HealthEventKind k) =>
        k is HealthEventKind.Error or HealthEventKind.PossibleDisconnect or HealthEventKind.FailedBattle or HealthEventKind.StoppedUnexpectedly;

    private static string Short(string s) => s.Length > 240 ? s[..240] + "…" : s;
}

using Exvs2Launcher.Core.Files;

namespace Exvs2Launcher.Core.Launch;

public static class ComponentRoles
{
    public const string CardServer = "Card server";
    public const string Lm = "LM";
    public const string Client = "Client";
    public static readonly string[] All = { CardServer, Lm, Client };
}

public sealed record RunningProcessInfo(int Pid, string? ExePath, string? ProcessName);

public interface IProcessControl
{
    IReadOnlyList<RunningProcessInfo> List();
    void KillTree(int pid);
}

public sealed record ComponentStatus(string Role, bool Running, int? Pid, string? Source)
{
    public string Label => Running
        ? $"Running (pid {Pid}{(Source is null ? "" : ", " + Source)})"
        : "Stopped";
}

/// <summary>
/// Finds the card server, LM, or client that belongs to one profile. A started PID is preferred; otherwise the
/// process exe path must be in this profile's game folder (or this profile's card-server folder). A process in
/// another cab folder is never a match, even if the exe name is the same.
/// </summary>
public static class ProfileProcessMatcher
{
    public static IReadOnlyList<string> ExpectedExePaths(string role, StartPlan plan)
    {
        var paths = new List<string>();
        if (string.IsNullOrWhiteSpace(plan.GameFolder) && role is ComponentRoles.Lm or ComponentRoles.Client)
            return paths;

        try
        {
            var game = string.IsNullOrWhiteSpace(plan.GameFolder) ? null : new GameFolder(plan.GameFolder);
            if (role == ComponentRoles.Client && game is not null) paths.Add(game.ClientExe);
            else if (role == ComponentRoles.Lm && game is not null) paths.Add(game.LmExe);
            else if (role == ComponentRoles.CardServer && !string.IsNullOrWhiteSpace(plan.CardServerPath))
            {
                var server = Path.GetFullPath(plan.CardServerPath);
                if (PortOwnerClassifier.IsExe(server)) paths.Add(server);
                var dir = Path.GetDirectoryName(server);
                if (!string.IsNullOrEmpty(dir))
                {
                    paths.Add(Path.Combine(dir, "Server-Infinite.exe"));
                    paths.Add(Path.Combine(dir, "server", "Server-Infinite.exe"));
                }
            }
        }
        catch (Exception) { }

        return paths
            .Select(p => { try { return Path.GetFullPath(p); } catch (Exception) { return p; } })
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public static bool PathBelongsToProfile(string? exePath, string role, StartPlan plan)
    {
        if (string.IsNullOrWhiteSpace(exePath)) return false;
        // Never match the stock Server.exe by path.
        try
        {
            if (string.Equals(Path.GetFileName(exePath), "Server.exe", StringComparison.OrdinalIgnoreCase))
                return false;
        }
        catch (Exception) { return false; }

        return ExpectedExePaths(role, plan).Any(expected => PortOwnerClassifier.SamePath(exePath, expected));
    }

    /// <summary>
    /// Processes to stop for this role: living PIDs this launcher started, then any running process whose exe
    /// path is one of the profile's expected paths. Name-only matches are ignored.
    /// </summary>
    public static IReadOnlyList<(int Pid, string How)> Find(
        string role,
        StartPlan plan,
        IEnumerable<IStartedProcess> started,
        IEnumerable<RunningProcessInfo> running)
    {
        var found = new List<(int Pid, string How)>();
        var seen = new HashSet<int>();

        foreach (var p in started)
        {
            if (p.Role != role || p.HasExited) continue;
            if (seen.Add(p.Id)) found.Add((p.Id, "started by this launcher"));
        }

        foreach (var r in running)
        {
            if (!PathBelongsToProfile(r.ExePath, role, plan)) continue;
            if (seen.Add(r.Pid)) found.Add((r.Pid, "this profile's folder"));
        }

        return found;
    }

    public static ComponentStatus Status(
        string role,
        StartPlan plan,
        IEnumerable<IStartedProcess> started,
        IEnumerable<RunningProcessInfo> running)
    {
        var hits = Find(role, plan, started, running);
        if (hits.Count == 0) return new ComponentStatus(role, false, null, null);
        var first = hits[0];
        return new ComponentStatus(role, true, first.Pid, first.How);
    }
}

using System.Diagnostics;

namespace Exvs2Launcher.Core.Files;

/// <summary>
/// Finds programs that would fight a file write: SnowLauncher keeps its ini in memory and rewrites the game files,
/// and the game reads card.ini / config.ini while running.
/// </summary>
public static class ProcessGuard
{
    public static readonly string[] ConflictingNames =
    {
        "SnowLauncher", "SnowLauncher_English", "vsac29_Release", "vsac29_Release_LM",
    };

    public static IReadOnlyList<string> RunningConflicts(Func<string, int>? countByName = null)
    {
        countByName ??= name =>
        {
            var ps = Process.GetProcessesByName(name);
            foreach (var p in ps) p.Dispose();
            return ps.Length;
        };
        return ConflictingNames.Where(n => countByName(n) > 0).Select(n => n + ".exe").ToList();
    }
}

using Exvs2Launcher.Core.Wizard;

namespace Exvs2Launcher.Core.Diagnostics;

/// <summary>Finds a card server's folder and its Serilog files (<c>Logs\log-yyyyMMdd.txt</c>) from a profile path.</summary>
public static class CardServerFiles
{
    public static string? ServerDir(string? cardServerPath)
    {
        if (string.IsNullOrWhiteSpace(cardServerPath)) return null;
        try
        {
            var exe = CardServerConfig.FindPatchedExe(cardServerPath);
            if (exe is not null) return Path.GetDirectoryName(exe);
            var full = Path.GetFullPath(cardServerPath.Trim().Trim('"'));
            var dir = File.Exists(full) ? Path.GetDirectoryName(full)! : full;
            if (Directory.Exists(Path.Combine(dir, "Logs"))) return dir;
            if (Directory.Exists(Path.Combine(dir, "server", "Logs"))) return Path.Combine(dir, "server");
            return Directory.Exists(dir) ? dir : null;
        }
        catch (Exception) { return null; }
    }

    public static string? LogsDir(string? cardServerPath)
    {
        var dir = ServerDir(cardServerPath);
        if (dir is null) return null;
        var logs = Path.Combine(dir, "Logs");
        return Directory.Exists(logs) ? logs : null;
    }

    /// <summary>Card server log files, newest first.</summary>
    public static IReadOnlyList<FileInfo> LogFiles(string? cardServerPath, int max = 3)
    {
        var logs = LogsDir(cardServerPath);
        if (logs is null) return Array.Empty<FileInfo>();
        try
        {
            return new DirectoryInfo(logs).GetFiles("log-*.txt")
                .OrderByDescending(f => f.LastWriteTimeUtc)
                .Take(Math.Max(0, max))
                .ToList();
        }
        catch (Exception) { return Array.Empty<FileInfo>(); }
    }
}

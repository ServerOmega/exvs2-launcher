namespace Exvs2Launcher.Core.CardServer;

/// <summary>What <see cref="CardServerSwap.Apply"/> did, so <see cref="CardServerSwap.Rollback"/> can undo it.</summary>
public sealed class SwapResult
{
    public required string ServerDir { get; init; }
    /// <summary>The previous server folder, moved aside whole (null on a fresh install).</summary>
    public string? BackupDir { get; init; }
    /// <summary>wwwroot\images was moved (not copied) from the backup into the new folder.</summary>
    public bool ImagesMoved { get; set; }
    public List<string> Kept { get; } = new();
    public List<string> Notes { get; } = new();
}

/// <summary>
/// Replaces a card server folder with a new build while keeping the user's data:
/// <list type="bullet">
/// <item><c>Configurations\</c> (server.json with MatchUrl / TagSecret, kestrel.json, log.json): the old files win;
/// the new build's defaults are saved next to them as <c>*.new-default</c>.</item>
/// <item>The card database <c>ServerInfinite.db</c> and its <c>-wal</c> / <c>-shm</c> files (copied, so the backup
/// keeps the pre-update database for a rollback even if the new server migrates it).</item>
/// <item><c>wwwroot\images</c> (moved; it can be large).</item>
/// <item>Other top-level data files the new build does not ship (leaderboard / friends / node-id state, salt files);
/// program files (.exe .dll .pdb .so) are always taken from the new build.</item>
/// </list>
/// The old folder is moved whole to <c>backups\server-&lt;stamp&gt;</c> first, so nothing is lost.
/// </summary>
public static class CardServerSwap
{
    public static readonly string[] DbFiles = { "ServerInfinite.db", "ServerInfinite.db-wal", "ServerInfinite.db-shm" };
    private static readonly HashSet<string> ProgramExtensions = new(StringComparer.OrdinalIgnoreCase)
        { ".exe", ".dll", ".pdb", ".so", ".dylib", ".bat", ".cmd" };

    public static SwapResult Apply(string newBuildDir, CardServerLayout layout, DateTime now)
    {
        if (!File.Exists(Path.Combine(newBuildDir, CardServerLayout.ExeName)))
            throw new InvalidOperationException("The new build has no " + CardServerLayout.ExeName + "; nothing was changed.");
        string? backup = null;
        if (Directory.Exists(layout.ServerDir))
        {
            Directory.CreateDirectory(layout.BackupsDir);
            backup = Unique(Path.Combine(layout.BackupsDir, "server-" + now.ToString("yyyyMMdd-HHmmss")));
            Directory.Move(layout.ServerDir, backup);
        }
        var result = new SwapResult { ServerDir = layout.ServerDir, BackupDir = backup };
        try
        {
            MoveOrCopy(newBuildDir, layout.ServerDir);
            if (backup is not null) KeepData(backup, layout.ServerDir, result);
            return result;
        }
        catch
        {
            Rollback(result);
            throw;
        }
    }

    private static void KeepData(string old, string dest, SwapResult result)
    {
        // Configurations: old settings win, new defaults saved beside them
        var oldCfg = Path.Combine(old, "Configurations");
        if (Directory.Exists(oldCfg))
            foreach (var f in Directory.EnumerateFiles(oldCfg, "*", SearchOption.AllDirectories))
            {
                var rel = Path.GetRelativePath(oldCfg, f);
                var target = Path.Combine(dest, "Configurations", rel);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                if (File.Exists(target) && !rel.Contains(".bak-", StringComparison.OrdinalIgnoreCase) &&
                    !rel.EndsWith(".new-default", StringComparison.OrdinalIgnoreCase))
                    File.Copy(target, target + ".new-default", overwrite: true);
                File.Copy(f, target, overwrite: true);
                result.Kept.Add(Path.Combine("Configurations", rel));
            }

        // database (copied: the backup keeps the pre-update DB)
        foreach (var db in DbFiles)
        {
            var f = Path.Combine(old, db);
            if (!File.Exists(f)) continue;
            File.Copy(f, Path.Combine(dest, db), overwrite: true);
            result.Kept.Add(db);
        }

        // other top-level data files the new build does not ship
        foreach (var f in Directory.EnumerateFiles(old))
        {
            var name = Path.GetFileName(f);
            if (DbFiles.Contains(name, StringComparer.OrdinalIgnoreCase)) continue;
            if (ProgramExtensions.Contains(Path.GetExtension(name))) continue;
            var target = Path.Combine(dest, name);
            if (File.Exists(target)) continue;
            File.Copy(f, target);
            result.Kept.Add(name);
        }

        // pictures: move when the new build has none, otherwise add the missing ones
        var oldImages = Path.Combine(old, "wwwroot", "images");
        if (Directory.Exists(oldImages))
        {
            var newImages = Path.Combine(dest, "wwwroot", "images");
            if (!Directory.Exists(newImages))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(newImages)!);
                Directory.Move(oldImages, newImages);
                result.ImagesMoved = true;
            }
            else
            {
                foreach (var f in Directory.EnumerateFiles(oldImages, "*", SearchOption.AllDirectories))
                {
                    var target = Path.Combine(newImages, Path.GetRelativePath(oldImages, f));
                    if (File.Exists(target)) continue;
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    File.Copy(f, target);
                }
            }
            result.Kept.Add(Path.Combine("wwwroot", "images"));
        }
    }

    /// <summary>Puts the previous folder back (the failed new one is kept under backups as failed-…).</summary>
    public static void Rollback(SwapResult r)
    {
        if (r.BackupDir is null)
        {
            // fresh install: just remove what was put there
            if (Directory.Exists(r.ServerDir)) TryDelete(r.ServerDir);
            return;
        }
        if (!Directory.Exists(r.BackupDir)) return;
        if (Directory.Exists(r.ServerDir))
        {
            if (r.ImagesMoved)
            {
                var src = Path.Combine(r.ServerDir, "wwwroot", "images");
                var dst = Path.Combine(r.BackupDir, "wwwroot", "images");
                if (Directory.Exists(src) && !Directory.Exists(dst))
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
                    Directory.Move(src, dst);
                }
                r.ImagesMoved = false;
            }
            var failed = Unique(Path.Combine(Path.GetDirectoryName(r.BackupDir)!, "failed-" + Path.GetFileName(r.BackupDir)));
            Directory.Move(r.ServerDir, failed);
            r.Notes.Add("The failed new build was kept in " + failed);
        }
        Directory.Move(r.BackupDir, r.ServerDir);
    }

    /// <summary>Keeps the newest <paramref name="keep"/> backups (and failed builds); older ones are deleted.</summary>
    public static IReadOnlyList<string> PruneBackups(CardServerLayout layout, int keep = 3)
    {
        var removed = new List<string>();
        if (!Directory.Exists(layout.BackupsDir)) return removed;
        foreach (var prefix in new[] { "server-", "failed-server-" })
            foreach (var d in Directory.GetDirectories(layout.BackupsDir, prefix + "*")
                         .Where(d => prefix != "server-" || !Path.GetFileName(d).StartsWith("failed-", StringComparison.Ordinal))
                         .OrderByDescending(Path.GetFileName, StringComparer.Ordinal).Skip(keep))
            {
                if (TryDelete(d)) removed.Add(d);
            }
        return removed;
    }

    internal static void MoveOrCopy(string src, string dest)
    {
        try
        {
            if (string.Equals(Path.GetPathRoot(Path.GetFullPath(src)), Path.GetPathRoot(Path.GetFullPath(dest)), StringComparison.OrdinalIgnoreCase))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(dest))!);
                Directory.Move(src, dest);
                return;
            }
        }
        catch (IOException) { }
        CopyDir(src, dest);
    }

    internal static void CopyDir(string src, string dest)
    {
        Directory.CreateDirectory(dest);
        foreach (var d in Directory.EnumerateDirectories(src, "*", SearchOption.AllDirectories))
            Directory.CreateDirectory(Path.Combine(dest, Path.GetRelativePath(src, d)));
        foreach (var f in Directory.EnumerateFiles(src, "*", SearchOption.AllDirectories))
            File.Copy(f, Path.Combine(dest, Path.GetRelativePath(src, f)), overwrite: true);
    }

    private static string Unique(string path)
    {
        var p = path;
        for (int n = 2; Directory.Exists(p) || File.Exists(p); n++) p = path + "-" + n;
        return p;
    }

    internal static bool TryDelete(string dir)
    {
        try { Directory.Delete(dir, recursive: true); return true; }
        catch (Exception) { return false; }
    }
}

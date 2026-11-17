using System.Globalization;
using Exvs2Launcher.Core.Files;
using Exvs2Launcher.Core.Identity;
using Exvs2Launcher.Core.Ini;

namespace Exvs2Launcher.Core.Cards;

public sealed record CardBackupInfo(string Path, DateTime CreatedAt, string Cab)
{
    public string Label => $"{CreatedAt:yyyy-MM-dd HH:mm:ss}  ({System.IO.Path.GetFileName(Path)})";
}

/// <summary>
/// Copies a cab's <c>card.ini</c> to the launcher's card backup folder and restores it. A restore first backs up the
/// current card.ini next to it (<c>card.ini.bak-…</c>) and is refused while that cab's game or LM is running.
/// Backups hold the card keys: they stay on this PC and are never put in a diagnostics bundle.
/// </summary>
public sealed class CardIniBackup
{
    private readonly string _root;
    private readonly Func<DateTime> _now;
    private readonly BackupService _backups;

    public CardIniBackup(string backupRoot, Func<DateTime>? now = null, BackupService? backups = null)
    {
        _root = backupRoot;
        _now = now ?? (() => DateTime.Now);
        _backups = backups ?? new BackupService(_now);
    }

    public string FolderFor(string cab) => Path.Combine(_root, Diagnostics.DiagnosticsBundleBuilder.SafeName(cab));

    /// <summary>Reads the card keys from a cab's card.ini (null when missing or not valid).</summary>
    public static CardIdentity? ReadCard(string gameFolder)
    {
        try
        {
            var path = new GameFolder(gameFolder).CardIni;
            if (!File.Exists(path)) return null;
            var doc = IniDocument.Load(path);
            var ac = IdentityFormat.NormalizeIniValue(doc.Get("card", "accessCode"));
            var chip = IdentityFormat.NormalizeIniValue(doc.Get("card", "chipId"));
            return IdentityFormat.IsValidAccessCode(ac) && IdentityFormat.IsValidChipId(chip) ? new CardIdentity(ac, chip) : null;
        }
        catch (Exception) { return null; }
    }

    public string Backup(string cab, string gameFolder)
    {
        var src = new GameFolder(gameFolder).CardIni;
        if (!File.Exists(src)) throw new FileNotFoundException("This cab has no card.ini to back up.", src);
        var dir = FolderFor(cab);
        Directory.CreateDirectory(dir);
        var target = Path.Combine(dir, $"card.ini.{_now():yyyyMMdd-HHmmss}");
        for (int n = 2; File.Exists(target); n++) target = Path.Combine(dir, $"card.ini.{_now():yyyyMMdd-HHmmss}-{n}");
        File.Copy(src, target);
        return target;
    }

    public IReadOnlyList<CardBackupInfo> List(string cab)
    {
        var dir = FolderFor(cab);
        if (!Directory.Exists(dir)) return Array.Empty<CardBackupInfo>();
        return new DirectoryInfo(dir).GetFiles("card.ini.*")
            .Select(f => new CardBackupInfo(f.FullName, ParseStamp(f.Name) ?? f.LastWriteTime, cab))
            .OrderByDescending(b => b.CreatedAt)
            .ToList();
    }

    /// <summary>Restores a backup into the cab folder. Returns the backup made of the replaced card.ini (if any).</summary>
    /// <param name="cabRunning">True while the cab's game or LM runs from this folder (restore is refused).</param>
    public string? Restore(string backupPath, string gameFolder, bool cabRunning)
    {
        if (cabRunning) throw new InvalidOperationException("Close this cab's game and LM before restoring its card.ini.");
        if (!File.Exists(backupPath)) throw new FileNotFoundException("Backup not found.", backupPath);
        var doc = IniDocument.Load(backupPath);
        var ac = IdentityFormat.NormalizeIniValue(doc.Get("card", "accessCode"));
        var chip = IdentityFormat.NormalizeIniValue(doc.Get("card", "chipId"));
        if (!IdentityFormat.IsValidAccessCode(ac) || !IdentityFormat.IsValidChipId(chip))
            throw new InvalidOperationException("That backup does not contain a valid card; nothing was changed.");
        var target = new GameFolder(gameFolder).CardIni;
        var replaced = _backups.Backup(target);
        var tmp = target + ".restore-tmp";
        File.Copy(backupPath, tmp, overwrite: true);
        File.Move(tmp, target, overwrite: true);
        return replaced;
    }

    private static DateTime? ParseStamp(string name)
    {
        var part = name.Replace("card.ini.", "");
        if (part.Length >= 15 && DateTime.TryParseExact(part[..15], "yyyyMMdd-HHmmss", CultureInfo.InvariantCulture, DateTimeStyles.None, out var t))
            return t;
        return null;
    }
}

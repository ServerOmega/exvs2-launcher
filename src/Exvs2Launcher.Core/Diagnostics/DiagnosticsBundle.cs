using System.Diagnostics;
using System.IO.Compression;
using System.Text;
using Exvs2Launcher.Core.Files;
using Exvs2Launcher.Core.Profiles;
using Exvs2Launcher.Core.Wizard;

namespace Exvs2Launcher.Core.Diagnostics;

/// <summary>One text file that will go into the diagnostics zip. Content is produced (and masked) when saved.</summary>
public sealed record BundleEntry(string ZipPath, string Description, Func<string> Content);

/// <summary>System facts the Windows app supplies (WMI / event log). Tests use a fake.</summary>
public interface ISystemInfoSource
{
    /// <summary>OS, CPU, RAM, GPU name and driver version / date. No serial numbers or unique ids.</summary>
    string SystemSummary();
    /// <summary>Recent Kernel-Power 41 and BugCheck 1001 events from the System log (time, id, short message).</summary>
    string CrashEventsSummary();
}

/// <summary>
/// Builds the "Diagnostics bundle" zip: launcher log, card server logs, profiles and settings with secrets masked,
/// system info, a crash event summary, and a top-level file listing of each cab folder (names, sizes, versions, no
/// hashes). Only text produced here goes into the zip; game files, executables and assets are never copied.
/// </summary>
public sealed class DiagnosticsBundleBuilder
{
    /// <summary>Only these extensions may be written into the zip (all are text this class generates).</summary>
    public static readonly IReadOnlySet<string> AllowedExtensions =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".txt", ".log", ".json", ".ini", ".md" };

    public const long MaxBytesPerLog = 2 * 1024 * 1024;

    private readonly ISystemInfoSource? _system;
    private readonly Func<DateTime> _now;

    public DiagnosticsBundleBuilder(ISystemInfoSource? system = null, Func<DateTime>? now = null)
    {
        _system = system;
        _now = now ?? (() => DateTime.Now);
    }

    public IReadOnlyList<BundleEntry> Plan(ProfileFile file, string? profilesPath, string? launcherLogPath,
        string? healthEventsDir = null, string? launcherVersion = null)
    {
        var list = new List<BundleEntry>();
        list.Add(new BundleEntry("README.txt", "What this bundle contains and what was masked",
            () => Readme(launcherVersion)));

        if (!string.IsNullOrWhiteSpace(launcherLogPath) && File.Exists(launcherLogPath))
            list.Add(new BundleEntry("launcher/exvs2-launcher.log", "Launcher log (last 2 MB, masked)",
                () => Redactor.MaskText(ReadTail(launcherLogPath, MaxBytesPerLog))));

        if (!string.IsNullOrWhiteSpace(profilesPath) && File.Exists(profilesPath))
            list.Add(new BundleEntry("launcher/profiles.json", "Launcher profiles and settings (masked)",
                () => Redactor.MaskJson(File.ReadAllText(profilesPath))));

        if (_system is not null)
        {
            list.Add(new BundleEntry("system/system.txt", "Windows version, CPU, RAM, GPU and driver", () => Safe(_system.SystemSummary)));
            list.Add(new BundleEntry("system/crash-events.txt", "Recent crash / unexpected restart events (System log 41 and 1001)",
                () => Redactor.MaskText(Safe(_system.CrashEventsSummary))));
        }

        var usedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var servers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in file.Profiles)
        {
            var cab = UniqueName(SafeName(p.Name), usedNames);
            if (!string.IsNullOrWhiteSpace(p.GameFolder) && Directory.Exists(p.GameFolder))
            {
                var game = new GameFolder(p.GameFolder);
                list.Add(new BundleEntry($"cabs/{cab}/listing.txt", $"{p.Name}: top-level file list (names, sizes, versions)",
                    () => Redactor.MaskText(Listing(game.Root))));
                foreach (var (path, name) in new[]
                         {
                             (game.ClientIni, "config.ini"), (game.LmIni, "lmnfig.ini"), (game.CardIni, "card.ini"),
                             (game.SnowLauncherIni, "SnowLauncher.ini"),
                         })
                {
                    if (!File.Exists(path)) continue;
                    var captured = path;
                    list.Add(new BundleEntry($"cabs/{cab}/{name}", $"{p.Name}: {name} (card values and secrets masked)",
                        () => Redactor.MaskIni(ReadText(captured))));
                }
            }

            var serverDir = CardServerFiles.ServerDir(p.CardServerPath);
            if (serverDir is null || !servers.Add(Path.GetFullPath(serverDir))) continue;
            var srv = UniqueName("server-" + SafeName(new DirectoryInfo(serverDir).Parent?.Name ?? new DirectoryInfo(serverDir).Name), usedNames);
            var json = CardServerConfig.FindServerJson(p.CardServerPath);
            if (json is not null)
                list.Add(new BundleEntry($"cardserver/{srv}/server.json", "Card server settings (tag secret and keys masked)",
                    () => Redactor.MaskJson(ReadText(json))));
            var kestrel = Path.Combine(serverDir, "Configurations", "kestrel.json");
            if (File.Exists(kestrel))
                list.Add(new BundleEntry($"cardserver/{srv}/kestrel.json", "Card server listen addresses (certificate password masked)",
                    () => Redactor.MaskJson(ReadText(kestrel))));
            foreach (var marker in new[] { "BUILD_COMMIT", "VERSION", "version.txt" })
            {
                var m = new[] { Path.Combine(serverDir, marker), Path.Combine(Path.GetDirectoryName(serverDir) ?? serverDir, "files", marker) }
                    .FirstOrDefault(File.Exists);
                if (m is null) continue;
                list.Add(new BundleEntry($"cardserver/{srv}/{marker.Replace(".txt", "")}.txt", "Card server version marker",
                    () => Redactor.MaskText(ReadTail(m, 4096))));
            }
            foreach (var log in CardServerFiles.LogFiles(p.CardServerPath, 3))
            {
                var path = log.FullName;
                list.Add(new BundleEntry($"cardserver/{srv}/logs/{SafeName(log.Name)}", $"Card server log {log.Name} (last 2 MB, masked)",
                    () => Redactor.MaskText(ReadTail(path, MaxBytesPerLog))));
            }
        }

        if (!string.IsNullOrWhiteSpace(healthEventsDir) && Directory.Exists(healthEventsDir))
        {
            foreach (var f in new DirectoryInfo(healthEventsDir).GetFiles("*.txt").OrderByDescending(f => f.LastWriteTimeUtc).Take(50))
            {
                var path = f.FullName;
                list.Add(new BundleEntry($"health/{SafeName(f.Name)}", "Saved health event (masked)",
                    () => Redactor.MaskText(ReadTail(path, 256 * 1024))));
            }
        }
        return list;
    }

    /// <summary>Writes the zip. Refuses entry names that are not plain text files or that try to leave the zip root.</summary>
    public static void Write(IEnumerable<BundleEntry> entries, string zipPath)
    {
        var list = entries.ToList();
        foreach (var e in list) Validate(e.ZipPath);
        var dir = Path.GetDirectoryName(Path.GetFullPath(zipPath));
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        var tmp = zipPath + ".part";
        using (var fs = File.Create(tmp))
        using (var zip = new ZipArchive(fs, ZipArchiveMode.Create))
        {
            foreach (var e in list)
            {
                string content;
                try { content = e.Content(); }
                catch (Exception ex) { content = "(could not read: " + Redactor.MaskText(ex.Message) + ")"; }
                var z = zip.CreateEntry(e.ZipPath, CompressionLevel.Optimal);
                using var w = new StreamWriter(z.Open(), new UTF8Encoding(false));
                w.Write(content);
            }
        }
        File.Move(tmp, zipPath, overwrite: true);
    }

    public static void Validate(string zipPath)
    {
        if (string.IsNullOrWhiteSpace(zipPath) || zipPath.Contains("..") || Path.IsPathRooted(zipPath) || zipPath.Contains('\\'))
            throw new InvalidOperationException("Refusing unsafe zip entry name: " + zipPath);
        var ext = Path.GetExtension(zipPath);
        if (!AllowedExtensions.Contains(ext))
            throw new InvalidOperationException("Refusing to put a non-text file into the diagnostics bundle: " + zipPath);
    }

    public string DefaultFileName() => $"exvs2-diagnostics-{_now():yyyyMMdd-HHmmss}.zip";

    /// <summary>Top-level names, sizes, dates and file versions. No hashes, no file contents.</summary>
    public static string Listing(string folder)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Folder: " + folder);
        sb.AppendLine("Top level only. Names, sizes, modified dates, and file versions for .exe/.dll. No hashes, no contents.");
        sb.AppendLine();
        try
        {
            var dir = new DirectoryInfo(folder);
            foreach (var d in dir.GetDirectories().OrderBy(d => d.Name, StringComparer.OrdinalIgnoreCase))
                sb.AppendLine($"<dir>  {d.Name}");
            foreach (var f in dir.GetFiles().OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase))
            {
                string version = "";
                if (f.Extension.Equals(".exe", StringComparison.OrdinalIgnoreCase) || f.Extension.Equals(".dll", StringComparison.OrdinalIgnoreCase))
                {
                    try
                    {
                        var vi = FileVersionInfo.GetVersionInfo(f.FullName);
                        var parts = new[] { vi.CompanyName, vi.ProductName, vi.FileVersion }.Where(x => !string.IsNullOrWhiteSpace(x));
                        version = string.Join(" | ", parts);
                    }
                    catch (Exception) { }
                }
                sb.AppendLine($"{f.Length,12:N0}  {f.LastWriteTime:yyyy-MM-dd HH:mm}  {f.Name}{(version.Length > 0 ? "  [" + version + "]" : "")}");
            }
        }
        catch (Exception ex) { sb.AppendLine("(could not list: " + ex.Message + ")"); }
        return sb.ToString();
    }

    private string Readme(string? version) =>
        $"""
        EXVS2 Launcher diagnostics bundle
        Created: {_now():yyyy-MM-dd HH:mm:ss} (local time)
        Launcher: {version ?? "unknown"}

        Contents: launcher log, launcher profiles/settings, config.ini / lmnfig.ini / card.ini / SnowLauncher.ini per cab,
        card server settings and recent logs, Windows / GPU / driver info, recent crash events, and a top-level file list
        of each cab folder.

        Masked: access codes, chip ids, card ids, tag secrets, boot tokens, passwords and other secret keys,
        20-digit and 32-hex card values, public IP addresses, and the Windows user name in paths.
        Not included: executables, DLLs, game data or assets, databases, certificates, file hashes.

        Please still read it before sharing.
        """;

    private static string ReadText(string path)
    {
        var bytes = File.ReadAllBytes(path);
        if (bytes.Length > MaxBytesPerLog) bytes = bytes[^(int)MaxBytesPerLog..];
        return Ini.TextFileCodec.Decode(bytes).Text;
    }

    /// <summary>Last <paramref name="max"/> bytes of a file that may be open for writing by the card server.</summary>
    public static string ReadTail(string path, long max)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        long start = Math.Max(0, fs.Length - max);
        fs.Seek(start, SeekOrigin.Begin);
        using var sr = new StreamReader(fs, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        var text = sr.ReadToEnd();
        if (start > 0)
        {
            int nl = text.IndexOf('\n');
            if (nl >= 0) text = text[(nl + 1)..];
            text = $"(first {start:N0} bytes skipped)\n" + text;
        }
        return text;
    }

    private static string Safe(Func<string> f)
    {
        try { return f(); }
        catch (Exception ex) { return "(not available: " + ex.Message + ")"; }
    }

    public static string SafeName(string? name)
    {
        var s = string.IsNullOrWhiteSpace(name) ? "cab" : name.Trim();
        var sb = new StringBuilder();
        foreach (var c in s)
            sb.Append(char.IsLetterOrDigit(c) || c is '-' or '_' or '.' ? c : '_');
        var r = sb.ToString().Trim('.');
        if (r.Length == 0) r = "cab";
        return r.Length > 40 ? r[..40] : r;
    }

    private static string UniqueName(string name, HashSet<string> used)
    {
        var n = name;
        for (int i = 2; !used.Add(n); i++) n = name + "-" + i;
        return n;
    }
}

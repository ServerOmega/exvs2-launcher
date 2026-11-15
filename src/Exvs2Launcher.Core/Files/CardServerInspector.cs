using System.Text.RegularExpressions;

namespace Exvs2Launcher.Core.Files;

/// <summary>What we could tell about the patched card server the profile points at (folder name or BUILD_COMMIT).</summary>
public sealed record CardServerInfo(string? VersionLabel, string? Source, bool LooksOld, string? Warning, string? Detail)
{
    public string? Summary =>
        VersionLabel is null && Detail is null ? null
        : string.Join(" — ", new[] { VersionLabel, Detail }.Where(s => !string.IsNullOrWhiteSpace(s)));
}

/// <summary>
/// Reads a version hint from the card server path (folder names like <c>exvs2-matchmaking-v0.4.1-pre</c>,
/// or a <c>BUILD_COMMIT</c> / <c>VERSION</c> file next to the exe or bat). Warns on early pre-releases
/// older than v0.4.0, which lack later matchmaking fixes.
/// </summary>
public static partial class CardServerInspector
{
    public static readonly Version MinimumRecommended = new(0, 4, 0);

    [GeneratedRegex(@"v?(?<maj>\d+)\.(?<min>\d+)\.(?<pat>\d+)(?<pre>-[A-Za-z0-9.]+)?", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex VersionRx();

    public static CardServerInfo Inspect(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return new CardServerInfo(null, null, false, null, null);

        string start;
        try { start = Path.GetFullPath(path.Trim()); }
        catch (Exception) { return new CardServerInfo(null, null, false, null, null); }

        string? dir = File.Exists(start) ? Path.GetDirectoryName(start) : (Directory.Exists(start) ? start : Path.GetDirectoryName(start));
        if (string.IsNullOrEmpty(dir)) return new CardServerInfo(null, null, false, null, null);

        string? label = null, source = null, detail = null;
        for (int hop = 0; hop < 6 && !string.IsNullOrEmpty(dir); hop++)
        {
            if (label is null)
            {
                var match = VersionRx().Match(Path.GetFileName(dir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)));
                if (match.Success)
                {
                    label = NormalizeLabel(match);
                    source = "folder " + Path.GetFileName(dir);
                }
            }
            if (detail is null)
            {
                foreach (var name in new[] { "BUILD_COMMIT", "VERSION", "version.txt" })
                {
                    var file = Path.Combine(dir, name);
                    if (!File.Exists(file)) continue;
                    string? text = TryReadShort(file);
                    if (string.IsNullOrWhiteSpace(text)) continue;
                    detail = name + " " + text.Trim();
                    label ??= text.Trim();
                    source ??= name + " in " + Path.GetFileName(dir);
                    break;
                }
            }
            if (label is not null && detail is not null) break;
            dir = Directory.GetParent(dir)?.FullName;
        }

        bool old = LooksLikeOldRelease(label);
        string? warning = old
            ? $"This card server looks like {label}, an early pre-release. Use a v{MinimumRecommended}+ package (v0.4.1-pre or later) from exvs2-matchmaking."
            : null;
        return new CardServerInfo(label, source, old, warning, detail);
    }

    internal static string NormalizeLabel(Match match)
    {
        var core = $"{match.Groups["maj"].Value}.{match.Groups["min"].Value}.{match.Groups["pat"].Value}";
        var pre = match.Groups["pre"].Value;
        return "v" + core + pre;
    }

    internal static bool LooksLikeOldRelease(string? label)
    {
        if (string.IsNullOrWhiteSpace(label)) return false;
        var match = VersionRx().Match(label);
        if (!match.Success) return false;
        if (!int.TryParse(match.Groups["maj"].Value, out int maj)) return false;
        if (!int.TryParse(match.Groups["min"].Value, out int min)) return false;
        if (!int.TryParse(match.Groups["pat"].Value, out int pat)) return false;
        var v = new Version(maj, min, pat);
        return v < MinimumRecommended;
    }

    private static string? TryReadShort(string path)
    {
        try
        {
            var info = new FileInfo(path);
            if (info.Length is <= 0 or > 1024) return null;
            var text = File.ReadAllText(path).Trim();
            // First non-empty line, short enough to show in the UI.
            var line = text.Split('\n').Select(s => s.Trim().Trim('\r')).FirstOrDefault(s => s.Length > 0);
            if (line is null || line.Length > 80) return null;
            return line;
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }
}

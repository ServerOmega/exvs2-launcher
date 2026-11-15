using Exvs2Launcher.Core.Files;

namespace Exvs2Launcher.Core.Profiles;

/// <summary>Collects the serials already used by other cabs on this PC, from profiles and their config.ini files.</summary>
public static class SerialRegistry
{
    public static IReadOnlyCollection<string> TakenSerials(ProfileFile file, LauncherProfile? except, GameConfigWriter? reader = null)
    {
        reader ??= new GameConfigWriter();
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in file.Profiles)
        {
            if (ReferenceEquals(p, except)) continue;
            if (!string.IsNullOrWhiteSpace(p.Serial)) set.Add(p.Serial);
            try
            {
                if (!string.IsNullOrWhiteSpace(p.GameFolder) && Directory.Exists(p.GameFolder))
                {
                    var s = reader.Read(p.GameFolder).Serial;
                    if (!string.IsNullOrWhiteSpace(s)) set.Add(s);
                }
            }
            catch (Exception) { /* unreadable folder: ignore */ }
        }
        return set;
    }

    /// <summary>Names of other profiles whose serial equals <paramref name="serial"/>.</summary>
    public static IReadOnlyList<string> Duplicates(ProfileFile file, LauncherProfile self, string? serial, GameConfigWriter? reader = null)
    {
        if (string.IsNullOrWhiteSpace(serial)) return Array.Empty<string>();
        reader ??= new GameConfigWriter();
        var dupes = new List<string>();
        foreach (var p in file.Profiles)
        {
            if (ReferenceEquals(p, self)) continue;
            string? other = p.Serial;
            try { if (Directory.Exists(p.GameFolder)) other = reader.Read(p.GameFolder).Serial ?? other; } catch (Exception) { }
            if (string.Equals(other, serial, StringComparison.OrdinalIgnoreCase)) dupes.Add(p.Name);
        }
        return dupes;
    }
}

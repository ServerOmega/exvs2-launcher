using System.Text;
using System.Text.Json;

namespace Exvs2Launcher.Core.VirtualLan;

/// <summary>
/// Keeps n2n keys out of the profiles JSON. Keys are stored per profile name in a separate file next to the
/// profiles, run through <paramref name="protect"/> / <paramref name="unprotect"/> (DPAPI for the current Windows
/// user in the app), so a copied file is useless on another account or PC. Never logs a key.
/// </summary>
public sealed class VirtualLanKeyStore
{
    public const string FileName = "exvs2-launcher.n2n-keys.dat";

    private readonly string _path;
    private readonly Func<byte[], byte[]> _protect;
    private readonly Func<byte[], byte[]> _unprotect;

    public VirtualLanKeyStore(string path, Func<byte[], byte[]> protect, Func<byte[], byte[]> unprotect)
    {
        _path = path;
        _protect = protect;
        _unprotect = unprotect;
    }

    public string FilePath => _path;

    public string? Get(string profileName)
    {
        var all = Load();
        return all.TryGetValue(profileName, out var k) ? k : null;
    }

    public bool Has(string profileName) => !string.IsNullOrEmpty(Get(profileName));

    private const string CommunitySuffix = "\u0001community";

    /// <summary>The community name is private too, so it is kept here rather than in the profiles JSON.</summary>
    public string? GetCommunity(string profileName) => Get(profileName + CommunitySuffix);

    public void SetCommunity(string profileName, string? community) => Set(profileName + CommunitySuffix, community);

    public void Set(string profileName, string? key)
    {
        var all = Load();
        if (string.IsNullOrEmpty(key)) all.Remove(profileName);
        else all[profileName] = key;
        Save(all);
    }

    public void Rename(string oldName, string newName)
    {
        if (string.Equals(oldName, newName, StringComparison.Ordinal)) return;
        var all = Load();
        bool changed = false;
        foreach (var suffix in new[] { "", CommunitySuffix })
        {
            if (!all.TryGetValue(oldName + suffix, out var k)) continue;
            all.Remove(oldName + suffix);
            all[newName + suffix] = k;
            changed = true;
        }
        if (changed) Save(all);
    }

    private Dictionary<string, string> Load()
    {
        try
        {
            if (!File.Exists(_path)) return new(StringComparer.Ordinal);
            var raw = _unprotect(File.ReadAllBytes(_path));
            return JsonSerializer.Deserialize<Dictionary<string, string>>(Encoding.UTF8.GetString(raw))
                   ?? new(StringComparer.Ordinal);
        }
        catch (Exception)
        {
            // Unreadable (another Windows user, or corrupted): behave as if no key was saved.
            return new(StringComparer.Ordinal);
        }
    }

    private void Save(Dictionary<string, string> all)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(_path));
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        if (all.Count == 0) { if (File.Exists(_path)) File.Delete(_path); return; }
        var tmp = _path + ".tmp";
        File.WriteAllBytes(tmp, _protect(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(all))));
        File.Move(tmp, _path, overwrite: true);
    }
}

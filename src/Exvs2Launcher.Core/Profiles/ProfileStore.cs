using System.Text.Json;
using System.Text.Json.Serialization;

namespace Exvs2Launcher.Core.Profiles;

public sealed class ProfileFile
{
    public int Version { get; set; } = 1;
    public string? LastSelected { get; set; }
    public List<LauncherProfile> Profiles { get; set; } = new();

    /// <summary>Saved matchmaking servers for the dropdown (name, host, port only). Null/empty = LA and EU placeholders.</summary>
    public List<Exvs2Launcher.Core.Matchmaking.MatchmakingServer>? MatchmakingServers { get; set; }

    /// <summary>Name of the server last picked in the dropdown.</summary>
    public string? SelectedMatchmakingServer { get; set; }

    /// <summary>Header of the last selected main-window tab (Play, Card Server, Matchmaking, Cab).</summary>
    public string? LastSelectedTab { get; set; }

    /// <summary>Card server updates: also offer GitHub pre-releases (all current exvs2-matchmaking releases are pre-releases).</summary>
    public bool CardServerIncludePrereleases { get; set; } = true;
}

/// <summary>
/// Saves profiles as <c>exvs2-launcher.profiles.json</c>. By default the file sits next to the launcher exe; if
/// that folder is read-only it falls back to %LocalAppData%\exvs2-launcher.
/// </summary>
public sealed class ProfileStore
{
    public const string FileName = "exvs2-launcher.profiles.json";

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        PropertyNameCaseInsensitive = true,
    };

    public ProfileStore(string path) => FilePath = path;

    public string FilePath { get; }

    public static ProfileStore Default()
    {
        var beside = System.IO.Path.Combine(AppContext.BaseDirectory, FileName);
        if (CanWrite(AppContext.BaseDirectory)) return new ProfileStore(beside);
        var dir = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "exvs2-launcher");
        Directory.CreateDirectory(dir);
        return new ProfileStore(System.IO.Path.Combine(dir, FileName));
    }

    public ProfileFile Load()
    {
        if (!File.Exists(FilePath)) return new ProfileFile();
        var text = File.ReadAllText(FilePath);
        return JsonSerializer.Deserialize<ProfileFile>(text, Json) ?? new ProfileFile();
    }

    public void Save(ProfileFile file)
    {
        var dir = System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(FilePath));
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        var tmp = FilePath + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(file, Json));
        File.Move(tmp, FilePath, overwrite: true);
    }

    /// <summary>One profile per game folder: finds an existing one (path compared case-insensitively).</summary>
    public static LauncherProfile? FindByFolder(ProfileFile file, string gameFolder)
    {
        string norm = Normalize(gameFolder);
        return file.Profiles.FirstOrDefault(p => Normalize(p.GameFolder) == norm);
    }

    internal static string Normalize(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return "";
        try { path = System.IO.Path.GetFullPath(path); } catch (Exception) { }
        return path.TrimEnd('\\', '/').ToUpperInvariant();
    }

    private static bool CanWrite(string dir)
    {
        try
        {
            var probe = System.IO.Path.Combine(dir, ".write-test-" + Environment.ProcessId);
            File.WriteAllText(probe, "");
            File.Delete(probe);
            return true;
        }
        catch (Exception) { return false; }
    }
}

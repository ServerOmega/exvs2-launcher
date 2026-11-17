using System.Text.Json;
using System.Text.Json.Nodes;

namespace Exvs2Launcher.Core.CardServer;

/// <summary>
/// Where a managed card server lives. <see cref="ServerDir"/> holds Server-Infinite.exe (the package's "server"
/// folder); <see cref="Root"/> is its parent (the package folder with 1-Build-Server.bat etc.). The launcher keeps
/// its downloads, build staging and backups under <see cref="WorkDir"/> next to the server.
/// </summary>
public sealed class CardServerLayout
{
    public const string ExeName = "Server-Infinite.exe";
    public const string WorkDirName = "exvs2-launcher-work";
    public const string StateFileName = "exvs2-launcher-cardserver.json";
    public const int DefaultPort = 28080;

    public CardServerLayout(string root, string serverDir)
    {
        Root = Path.GetFullPath(root);
        ServerDir = Path.GetFullPath(serverDir);
    }

    public string Root { get; }
    public string ServerDir { get; }
    public string ExePath => Path.Combine(ServerDir, ExeName);
    public string WorkDir => Path.Combine(Root, WorkDirName);
    public string DownloadsDir => Path.Combine(WorkDir, "downloads");
    public string StagingDir => Path.Combine(WorkDir, "staging");
    public string BackupsDir => Path.Combine(WorkDir, "backups");
    public string StateFile => Path.Combine(Root, StateFileName);
    public bool IsInstalled => File.Exists(ExePath);
    /// <summary>The package folder layout (files\VERSION next to server\) from the release zip.</summary>
    public bool IsPackageRoot => File.Exists(Path.Combine(Root, "files", "VERSION"));

    /// <summary>A new or existing install folder chosen by the user: the server goes into &lt;folder&gt;\server.</summary>
    public static CardServerLayout ForInstallFolder(string folder) =>
        new(folder, Path.Combine(folder, "server"));

    /// <summary>
    /// Finds an existing card server from a path the profile already has: Server-Infinite.exe, 3-Start-Server.bat,
    /// the server folder or the package folder. Null when there is no Server-Infinite.exe there.
    /// </summary>
    public static CardServerLayout? Detect(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        string full;
        try { full = Path.GetFullPath(path.Trim().Trim('"')); }
        catch (Exception) { return null; }
        var dir = File.Exists(full) ? Path.GetDirectoryName(full)! : full;
        if (File.Exists(Path.Combine(dir, ExeName)))
        {
            var parent = Directory.GetParent(dir)?.FullName ?? dir;
            return new CardServerLayout(parent, dir);
        }
        if (File.Exists(Path.Combine(dir, "server", ExeName)))
            return new CardServerLayout(dir, Path.Combine(dir, "server"));
        return null;
    }

    /// <summary>The card server's own port from Configurations\kestrel.json (first endpoint), default 28080.</summary>
    public int ReadPort()
    {
        try
        {
            var path = Path.Combine(ServerDir, "Configurations", "kestrel.json");
            if (!File.Exists(path)) return DefaultPort;
            var node = JsonNode.Parse(File.ReadAllText(path),
                documentOptions: new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
            if (node?["Kestrel"]?["Endpoints"] is JsonObject eps)
                foreach (var ep in eps)
                {
                    var url = ep.Value?["Url"]?.GetValue<string>();
                    if (url is null) continue;
                    var fixedUrl = url.Replace("://*", "://localhost").Replace("://+", "://localhost").Replace("://0.0.0.0", "://localhost");
                    if (Uri.TryCreate(fixedUrl, UriKind.Absolute, out var u) && u.Port > 0) return u.Port;
                }
        }
        catch (Exception) { }
        return DefaultPort;
    }

    /// <summary>The installed version: the launcher's state file, else the package's files\VERSION.</summary>
    public string? InstalledVersion()
    {
        var st = InstallState.Load(StateFile);
        if (!string.IsNullOrWhiteSpace(st?.Tag)) return st!.Tag;
        try
        {
            var v = Path.Combine(Root, "files", "VERSION");
            if (File.Exists(v)) return File.ReadAllText(v).Trim();
        }
        catch (Exception) { }
        return null;
    }
}

/// <summary>What the launcher installed last (no secrets).</summary>
public sealed class InstallState
{
    public string? Tag { get; set; }
    public string? Kind { get; set; }
    public string? Sha256 { get; set; }
    public DateTimeOffset? InstalledAt { get; set; }
    public string? PreviousTag { get; set; }
    public string? LastBackup { get; set; }

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, PropertyNameCaseInsensitive = true };

    public static InstallState? Load(string path)
    {
        try { return File.Exists(path) ? JsonSerializer.Deserialize<InstallState>(File.ReadAllText(path), Json) : null; }
        catch (Exception) { return null; }
    }

    public void Save(string path)
    {
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(this, Json));
        File.Move(tmp, path, overwrite: true);
    }
}

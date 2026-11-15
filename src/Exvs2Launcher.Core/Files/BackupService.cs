namespace Exvs2Launcher.Core.Files;

/// <summary>Copies a file to <c>name.bak-yyyyMMdd-HHmmss</c> (adding -2, -3... if that already exists).</summary>
public sealed class BackupService
{
    private readonly Func<DateTime> _now;

    public BackupService() : this(() => DateTime.Now) { }
    public BackupService(Func<DateTime> now) => _now = now;

    public string? Backup(string path)
    {
        if (!File.Exists(path)) return null;
        string stamp = _now().ToString("yyyyMMdd-HHmmss");
        string target = $"{path}.bak-{stamp}";
        for (int n = 2; File.Exists(target); n++) target = $"{path}.bak-{stamp}-{n}";
        File.Copy(path, target);
        return target;
    }
}

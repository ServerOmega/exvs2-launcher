using System.Diagnostics;

namespace Exvs2Launcher.Core.Launch;

/// <summary>
/// The game names its own window (often <c>PCB_03</c>, <c>PCB_14</c>, and similar). That title is not
/// stored in a launcher-editable INI key, so this helper only reports the running window and the
/// game-folder name. The cab name in the launcher profile is the label we show instead.
/// </summary>
public static class GameWindowTitle
{
    public static string? FolderLabel(string? gameFolder)
    {
        if (string.IsNullOrWhiteSpace(gameFolder)) return null;
        var trimmed = gameFolder.Trim().TrimEnd('\\', '/');
        if (trimmed.Length == 0) return null;
        // Normalize separators so a Windows path still yields the folder name when tests run on Linux.
        var name = Path.GetFileName(trimmed.Replace('\\', '/'));
        return string.IsNullOrWhiteSpace(name) ? null : name;
    }

    public static string? TryRead(int pid)
    {
        if (!OperatingSystem.IsWindows() || pid <= 0) return null;
        try
        {
            using var p = Process.GetProcessById(pid);
            var title = p.MainWindowTitle;
            return string.IsNullOrWhiteSpace(title) ? null : title.Trim();
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// A one-line mapping such as <c>folder EXVS2IB_V29; game window currently PCB_03</c>,
    /// or a note that the PCB_xx title is unknown until the client is running.
    /// </summary>
    public static string Describe(string? folderLabel, string? windowTitle)
    {
        var folder = string.IsNullOrWhiteSpace(folderLabel) ? null : folderLabel.Trim();
        var window = string.IsNullOrWhiteSpace(windowTitle) ? null : windowTitle.Trim();
        if (folder is not null && window is not null)
            return "Game folder " + folder + "; game window currently " + window + ".";
        if (folder is not null)
            return "Game folder " + folder + ". The game names its own window (often PCB_xx); that title appears here once the client is running. This launcher cannot rename it.";
        if (window is not null)
            return "Game window currently " + window + ".";
        return "The game names its own window (often PCB_xx). This launcher cannot rename it; use the cab name above in the title bar and log.";
    }
}

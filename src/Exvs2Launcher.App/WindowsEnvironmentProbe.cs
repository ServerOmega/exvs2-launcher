using System.Diagnostics;
using System.IO;
using Exvs2Launcher.Core.Wizard;
using Microsoft.Win32;

namespace Exvs2Launcher.App;

/// <summary>Reads local files, process names, and service registry keys. Never changes firewall rules.</summary>
public sealed class WindowsEnvironmentProbe : IEnvironmentProbe
{
    public bool FileExists(string path) => File.Exists(path);

    public bool ProcessNameRunning(string name)
    {
        try { return Process.GetProcessesByName(name).Length > 0; }
        catch (Exception) { return false; }
    }

    public bool ServiceRunning(string serviceName)
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\" + serviceName);
            if (key is null) return false;
            // Start: 2 = automatic, 3 = manual, 4 = disabled. Disabled is treated as not in use.
            return key.GetValue("Start") is not int start || start != 4;
        }
        catch (Exception) { return false; }
    }

    public IReadOnlyList<string> ProgramFilesRoots()
    {
        var list = new List<string>();
        foreach (var ev in new[] { "ProgramFiles", "ProgramFiles(x86)", "ProgramW6432" })
        {
            var v = Environment.GetEnvironmentVariable(ev);
            if (!string.IsNullOrWhiteSpace(v)) list.Add(v);
        }
        return list.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }
}

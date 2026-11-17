using System.Diagnostics.Eventing.Reader;
using System.Management;
using System.Runtime.InteropServices;
using System.Text;
using Exvs2Launcher.Core.Diagnostics;

namespace Exvs2Launcher.App;

/// <summary>
/// OS / CPU / RAM / GPU facts from WMI and recent Kernel-Power 41 / BugCheck 1001 events. Read-only; no serial
/// numbers, machine ids or hardware unique ids are collected.
/// </summary>
public sealed class WindowsSystemInfo : ISystemInfoSource
{
    public string SystemSummary()
    {
        var sb = new StringBuilder();
        sb.AppendLine("OS: " + RuntimeInformation.OSDescription + " (" + RuntimeInformation.OSArchitecture + ")");
        Query(sb, "Win32_OperatingSystem", "Caption, Version, BuildNumber, TotalVisibleMemorySize",
            o => $"Windows: {o["Caption"]} {o["Version"]} build {o["BuildNumber"]}, RAM {Kb(o["TotalVisibleMemorySize"])}");
        Query(sb, "Win32_Processor", "Name, NumberOfCores, NumberOfLogicalProcessors",
            o => $"CPU: {Clean(o["Name"])} ({o["NumberOfCores"]} cores / {o["NumberOfLogicalProcessors"]} threads)");
        Query(sb, "Win32_VideoController", "Name, DriverVersion, DriverDate, AdapterRAM, CurrentHorizontalResolution, CurrentVerticalResolution, CurrentRefreshRate",
            o => $"GPU: {Clean(o["Name"])}, driver {o["DriverVersion"]} ({Date(o["DriverDate"])}), " +
                 $"{o["CurrentHorizontalResolution"]}x{o["CurrentVerticalResolution"]} @ {o["CurrentRefreshRate"]} Hz");
        Query(sb, "Win32_SoundDevice", "Name", o => $"Audio: {Clean(o["Name"])}");
        sb.AppendLine(".NET: " + RuntimeInformation.FrameworkDescription);
        return sb.ToString();
    }

    public string CrashEventsSummary()
    {
        var sb = new StringBuilder();
        sb.AppendLine("Kernel-Power 41 (unexpected shutdown / power loss) and BugCheck 1001 (blue screen) in the last 30 days:");
        try
        {
            const string xpath = "*[System[(EventID=41 or EventID=1001) and TimeCreated[timediff(@SystemTime) <= 2592000000]]]";
            var q = new EventLogQuery("System", PathType.LogName, xpath) { ReverseDirection = true };
            using var reader = new EventLogReader(q);
            int n = 0;
            for (var ev = reader.ReadEvent(); ev is not null && n < 50; ev = reader.ReadEvent())
            {
                using (ev)
                {
                    if (ev.Id == 1001 && ev.ProviderName?.Contains("WER", StringComparison.OrdinalIgnoreCase) == true) continue;
                    string msg;
                    try { msg = ev.FormatDescription() ?? ""; } catch { msg = ""; }
                    msg = msg.Replace("\r", " ").Replace("\n", " ");
                    if (msg.Length > 300) msg = msg[..300] + "…";
                    sb.AppendLine($"{ev.TimeCreated:yyyy-MM-dd HH:mm:ss}  id {ev.Id}  {ev.ProviderName}  {msg}");
                    n++;
                }
            }
            if (n == 0) sb.AppendLine("(none)");
        }
        catch (Exception ex) { sb.AppendLine("(could not read the System event log: " + ex.Message + ")"); }
        return sb.ToString();
    }

    private static void Query(StringBuilder sb, string cls, string props, Func<ManagementBaseObject, string> line)
    {
        try
        {
            using var s = new ManagementObjectSearcher($"SELECT {props} FROM {cls}");
            foreach (var o in s.Get()) using (o) sb.AppendLine(line(o));
        }
        catch (Exception ex) { sb.AppendLine($"{cls}: not available ({ex.Message})"); }
    }

    private static string Clean(object? v) => (v?.ToString() ?? "").Trim();

    private static string Kb(object? v) =>
        ulong.TryParse(v?.ToString(), out var kb) ? $"{kb / 1024.0 / 1024.0:0.0} GB" : "?";

    private static string Date(object? v)
    {
        var s = v?.ToString();
        if (string.IsNullOrEmpty(s)) return "?";
        try { return ManagementDateTimeConverter.ToDateTime(s).ToString("yyyy-MM-dd"); } catch { return s; }
    }
}

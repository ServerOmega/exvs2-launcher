using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using Exvs2Launcher.Core.Launch;
using Exvs2Launcher.Core.VirtualLan;

namespace Exvs2Launcher.App;

/// <summary>
/// Starts the user's edge.exe with administrator rights (one UAC prompt) through cmd.exe so its output goes to a
/// log file the launcher can read. Only the config-file path is on the command line; the key is in that file,
/// which the controller deletes as soon as the TAP is up. The launcher itself stays asInvoker.
/// </summary>
public sealed class ElevatedEdgeHost : IEdgeHost
{
    public IStartedProcess Start(EdgeCommand command, string logPath)
    {
        var dir = Path.GetDirectoryName(command.ExePath) ?? Environment.CurrentDirectory;
        // cmd /c ""exe" "conf" > "log" 2>&1": the outer quotes keep cmd from stripping the inner ones.
        var inner = $"\"\"{command.ExePath}\" \"{command.ConfigPath}\" > \"{logPath}\" 2>&1\"";
        var psi = new ProcessStartInfo("cmd.exe", "/d /c " + inner)
        {
            UseShellExecute = true,
            Verb = "runas",
            WorkingDirectory = dir,
            WindowStyle = ProcessWindowStyle.Hidden,
        };
        try
        {
            var p = Process.Start(psi) ?? throw new InvalidOperationException("Windows did not start edge.exe.");
            return new EdgeProcess(p);
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            throw new InvalidOperationException("The Windows administrator prompt was cancelled.", ex);
        }
    }

    public string ForceStop(IStartedProcess process)
    {
        try
        {
            var psi = new ProcessStartInfo("taskkill.exe", $"/PID {process.Id} /T /F")
            {
                UseShellExecute = true,
                Verb = "runas",
                WindowStyle = ProcessWindowStyle.Hidden,
            };
            using var p = Process.Start(psi);
            p?.WaitForExit(5000);
            return "Closed it with an elevated taskkill. The supernode may keep this MAC/IP for about 2 minutes.";
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            return "The administrator prompt to close it was cancelled; close edge.exe in Task Manager.";
        }
        catch (Exception ex)
        {
            return "Could not close it (" + ex.Message + "); close edge.exe in Task Manager.";
        }
    }

    private sealed class EdgeProcess : IStartedProcess
    {
        private readonly Process _p;
        public EdgeProcess(Process p) { _p = p; Id = p.Id; }
        public string Role => "n2n edge";
        public int Id { get; }
        public bool HasExited { get { try { return _p.HasExited; } catch (Exception) { return true; } } }
        public int? ExitCode { get { try { return _p.HasExited ? _p.ExitCode : null; } catch (Exception) { return null; } } }
        public void Kill()
        {
            try { if (!_p.HasExited) _p.Kill(entireProcessTree: true); }
            catch (Exception) { }
        }
    }
}

/// <summary>DPAPI (current Windows user) protection for the n2n key file.</summary>
public static class Dpapi
{
    private static readonly byte[] Entropy = "exvs2-launcher n2n key v1"u8.ToArray();

    public static byte[] Protect(byte[] data) =>
        ProtectedData.Protect(data, Entropy, DataProtectionScope.CurrentUser);

    public static byte[] Unprotect(byte[] data) =>
        ProtectedData.Unprotect(data, Entropy, DataProtectionScope.CurrentUser);
}

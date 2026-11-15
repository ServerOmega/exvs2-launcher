using System.ComponentModel;
using System.Diagnostics;
using System.Net.Sockets;
using System.Runtime.InteropServices;

namespace Exvs2Launcher.Core.Launch;

/// <summary>
/// Starts programs the way a double-click does (own console window for .bat/.exe servers), with the given working
/// directory. No arguments are added beyond the profile's own.
/// </summary>
public sealed class SystemProcessStarter : IProcessStarter
{
    public IStartedProcess Start(LaunchSpec spec)
    {
        var psi = new ProcessStartInfo(spec.FileName)
        {
            Arguments = spec.Arguments,
            WorkingDirectory = spec.WorkingDirectory,
            UseShellExecute = true,
        };
        var p = Process.Start(psi) ?? throw new InvalidOperationException($"Windows did not start {spec.FileName}.");
        return new SystemStartedProcess(spec.Role, p);
    }

    private sealed class SystemStartedProcess : IStartedProcess
    {
        private readonly Process _p;
        public SystemStartedProcess(string role, Process p) { Role = role; _p = p; Id = p.Id; }
        public string Role { get; }
        public int Id { get; }
        public bool HasExited { get { try { return _p.HasExited; } catch (InvalidOperationException) { return true; } } }
        public int? ExitCode { get { try { return _p.HasExited ? _p.ExitCode : null; } catch (InvalidOperationException) { return null; } } }
        public void Kill()
        {
            try { if (!_p.HasExited) _p.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) { }
            catch (Win32Exception) { }
        }
    }
}

public sealed class TcpPortProbe : IPortProbe
{
    public async Task<bool> IsListeningAsync(string host, int port, CancellationToken ct)
    {
        using var client = new TcpClient();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(1));
        try
        {
            await client.ConnectAsync(host, port, timeout.Token).ConfigureAwait(false);
            return client.Connected;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return false; }
        catch (SocketException) { return false; }
    }
}

/// <summary>Finds the PID that owns a TCP listener using the Windows IP Helper API (GetExtendedTcpTable).</summary>
public sealed class WindowsPortOwnerLookup : IPortOwnerLookup
{
    private const int AfInet = 2, AfInet6 = 23, TcpTableOwnerPidListener = 3;

    [DllImport("iphlpapi.dll", SetLastError = true)]
    private static extern uint GetExtendedTcpTable(IntPtr table, ref int size, bool sort, int ipVersion, int tableClass, uint reserved);

    public PortOwner? FindListener(int port)
    {
        if (!OperatingSystem.IsWindows()) return null;
        int? pid = Find(AfInet, port, rowSize: 24, portOffset: 8, pidOffset: 20)
                ?? Find(AfInet6, port, rowSize: 56, portOffset: 20, pidOffset: 52);
        if (pid is null) return null;
        string? name = null, path = null;
        try
        {
            using var p = Process.GetProcessById(pid.Value);
            name = p.ProcessName;
            try { path = p.MainModule?.FileName; } catch (Win32Exception) { } catch (InvalidOperationException) { }
        }
        catch (ArgumentException) { }
        return new PortOwner(pid.Value, name, path);
    }

    private static int? Find(int family, int port, int rowSize, int portOffset, int pidOffset)
    {
        int size = 0;
        GetExtendedTcpTable(IntPtr.Zero, ref size, false, family, TcpTableOwnerPidListener, 0);
        if (size <= 0) return null;
        IntPtr buf = Marshal.AllocHGlobal(size);
        try
        {
            if (GetExtendedTcpTable(buf, ref size, false, family, TcpTableOwnerPidListener, 0) != 0) return null;
            int count = Marshal.ReadInt32(buf);
            for (int i = 0; i < count; i++)
            {
                IntPtr row = buf + 4 + i * rowSize;
                uint raw = (uint)Marshal.ReadInt32(row + portOffset);
                int p = (int)(((raw & 0xFF) << 8) | ((raw >> 8) & 0xFF));
                if (p == port) return Marshal.ReadInt32(row + pidOffset);
            }
            return null;
        }
        finally { Marshal.FreeHGlobal(buf); }
    }
}

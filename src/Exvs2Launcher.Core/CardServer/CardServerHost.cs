using System.ComponentModel;
using System.Diagnostics;
using Exvs2Launcher.Core.Launch;

namespace Exvs2Launcher.Core.CardServer;

/// <summary>Starts, finds and stops the managed card server process (Server-Infinite.exe in one folder).</summary>
public interface ICardServerHost
{
    /// <summary>PIDs of Server-Infinite.exe processes whose exe is inside <paramref name="serverDir"/>.</summary>
    IReadOnlyList<int> FindRunning(string serverDir);
    int Start(string exePath, string workingDirectory);
    /// <summary>Asks the server window to close (graceful shutdown), then kills it after <paramref name="grace"/>.</summary>
    Task StopAsync(int pid, TimeSpan grace, CancellationToken ct);
    bool IsAlive(int pid);
}

public sealed class SystemCardServerHost : ICardServerHost
{
    private readonly IProcessControl _processes;

    public SystemCardServerHost(IProcessControl? processes = null) => _processes = processes ?? new SystemProcessControl();

    public IReadOnlyList<int> FindRunning(string serverDir)
    {
        var dir = Path.GetFullPath(serverDir).TrimEnd('\\', '/') + Path.DirectorySeparatorChar;
        return _processes.List()
            .Where(p => p.ExePath is not null &&
                        string.Equals(Path.GetFileName(p.ExePath), CardServerLayout.ExeName, StringComparison.OrdinalIgnoreCase) &&
                        p.ExePath.StartsWith(dir, StringComparison.OrdinalIgnoreCase))
            .Select(p => p.Pid).ToList();
    }

    public int Start(string exePath, string workingDirectory)
    {
        // own console window, like a double-click (the server shows its log there and a 5 s notice at start)
        var p = Process.Start(new ProcessStartInfo(exePath) { WorkingDirectory = workingDirectory, UseShellExecute = true })
                ?? throw new InvalidOperationException("Windows did not start " + exePath);
        return p.Id;
    }

    public async Task StopAsync(int pid, TimeSpan grace, CancellationToken ct)
    {
        Process p;
        try { p = Process.GetProcessById(pid); }
        catch (ArgumentException) { return; }
        using (p)
        {
            try
            {
                if (p.HasExited) return;
                p.CloseMainWindow();   // console close = graceful host shutdown (SQLite flushed)
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(grace);
                try { await p.WaitForExitAsync(cts.Token).ConfigureAwait(false); return; }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested) { }
                if (!p.HasExited) p.Kill(entireProcessTree: true);
                await p.WaitForExitAsync(ct).ConfigureAwait(false);
            }
            catch (InvalidOperationException) { }
            catch (Win32Exception) { }
        }
    }

    public bool IsAlive(int pid)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            return !p.HasExited;
        }
        catch (ArgumentException) { return false; }
        catch (InvalidOperationException) { return false; }
    }
}

/// <summary>Runs the release package's own build script (1-Build-Server.bat's PowerShell step) on this PC.</summary>
public interface IBuildRunner
{
    /// <summary>Returns the script's exit code.</summary>
    Task<int> RunAsync(string packageDir, CancellationToken ct);
}

public sealed class PowerShellBuildRunner : IBuildRunner
{
    public async Task<int> RunAsync(string packageDir, CancellationToken ct)
    {
        var script = Path.Combine(packageDir, "scripts", "build-server.ps1");
        if (!File.Exists(script)) throw new FileNotFoundException("The package has no scripts\\build-server.ps1.", script);
        // a visible window so the user can follow the build (downloads .NET / source on the first run)
        var psi = new ProcessStartInfo("powershell.exe",
            $"-NoProfile -ExecutionPolicy Bypass -File \"{script}\" -NoPrompt")
        {
            WorkingDirectory = packageDir,
            UseShellExecute = true,
        };
        using var p = Process.Start(psi) ?? throw new InvalidOperationException("Could not start PowerShell for the build.");
        try { await p.WaitForExitAsync(ct).ConfigureAwait(false); }
        catch (OperationCanceledException)
        {
            try { if (!p.HasExited) p.Kill(entireProcessTree: true); } catch (Exception) { }
            throw;
        }
        return p.ExitCode;
    }
}

public interface IFileDownloader
{
    Task DownloadAsync(string url, string destination, IProgress<long>? progress, CancellationToken ct);
    Task<string> GetStringAsync(string url, CancellationToken ct);
}

public sealed class HttpFileDownloader : IFileDownloader
{
    public const long MaxDownloadBytes = 2L * 1024 * 1024 * 1024;
    private readonly HttpClient _http;

    public HttpFileDownloader(HttpClient http) => _http = http;

    public async Task DownloadAsync(string url, string destination, IProgress<long>? progress, CancellationToken ct)
    {
        RequireHttps(url);
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.UserAgent.ParseAdd("exvs2-launcher");
        using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(destination))!);
        var tmp = destination + ".part";
        await using (var src = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false))
        await using (var dst = File.Create(tmp))
        {
            var buf = new byte[81920];
            long total = 0;
            int n;
            while ((n = await src.ReadAsync(buf, ct).ConfigureAwait(false)) > 0)
            {
                total += n;
                if (total > MaxDownloadBytes) throw new InvalidOperationException("The download is larger than expected; stopped.");
                await dst.WriteAsync(buf.AsMemory(0, n), ct).ConfigureAwait(false);
                progress?.Report(total);
            }
        }
        File.Move(tmp, destination, overwrite: true);
    }

    public async Task<string> GetStringAsync(string url, CancellationToken ct)
    {
        RequireHttps(url);
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.UserAgent.ParseAdd("exvs2-launcher");
        using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
        var s = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        return s.Length > 4096 ? s[..4096] : s;
    }

    private static void RequireHttps(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var u) || u.Scheme != Uri.UriSchemeHttps)
            throw new InvalidOperationException("Only https downloads are allowed: " + url);
    }
}

using System.IO.Compression;
using Exvs2Launcher.Core.Launch;

namespace Exvs2Launcher.Core.CardServer;

public sealed record CardServerState(bool Installed, string? Version, IReadOnlyList<int> RunningPids, int Port, bool Listening)
{
    public bool Running => RunningPids.Count > 0;

    public string Summary =>
        !Installed ? "Not installed"
        : (Running ? $"Running (pid {string.Join(", ", RunningPids)})" + (Listening ? $", listening on {Port}" : $", not listening on {Port} yet")
                   : "Stopped") + (Version is null ? "" : " · version " + Version);
}

public sealed record UpdateCheck(string? Installed, PackageChoice? Latest, bool UpdateAvailable, string? Error)
{
    public string Summary =>
        Error is not null ? "Could not check: " + Error
        : Latest is null ? "No installable release found."
        : UpdateAvailable ? $"{Latest.Release.Label} is available" + (Installed is null ? "" : $" (installed: {Installed})")
        : $"Up to date ({Installed})";
}

public sealed class InstallOptions
{
    /// <summary>The user confirmed no cab is in a match or loading (required to update a running install).</summary>
    public bool CabsIdleConfirmed { get; init; }
    /// <summary>Start the server after a fresh install (an update always restarts to verify it).</summary>
    public bool StartAfterFreshInstall { get; init; }
    public TimeSpan HealthTimeout { get; init; } = TimeSpan.FromSeconds(90);
    public TimeSpan StopGrace { get; init; } = TimeSpan.FromSeconds(15);
}

public sealed record InstallOutcome(bool Success, bool RolledBack, string Message, string? BackupDir);

/// <summary>
/// Installs and updates a card server from the exvs2-matchmaking GitHub releases. Order: download + SHA-256 check +
/// prepare the new build while the old server keeps running → stop it → move the old folder to a backup and swap in
/// the new one keeping Configurations, the database, pictures and data files → start → wait until it listens on its
/// port → on any failure put the old folder back and restart it.
/// </summary>
public sealed class CardServerManager
{
    private readonly IReleaseSource _releases;
    private readonly IFileDownloader _downloader;
    private readonly ICardServerHost _host;
    private readonly IPortProbe _probe;
    private readonly IPortOwnerLookup? _owners;
    private readonly IBuildRunner _builder;
    private readonly IDelay _delay;
    private readonly Func<DateTime> _now;

    public CardServerManager(IReleaseSource releases, IFileDownloader downloader, ICardServerHost host, IPortProbe probe,
        IBuildRunner builder, IPortOwnerLookup? owners = null, IDelay? delay = null, Func<DateTime>? now = null)
    {
        _releases = releases;
        _downloader = downloader;
        _host = host;
        _probe = probe;
        _builder = builder;
        _owners = owners;
        _delay = delay ?? new RealDelay();
        _now = now ?? (() => DateTime.Now);
    }

    public async Task<CardServerState> GetStateAsync(CardServerLayout layout, CancellationToken ct = default)
    {
        var port = layout.ReadPort();
        var pids = layout.IsInstalled ? _host.FindRunning(layout.ServerDir) : Array.Empty<int>();
        var listening = pids.Count > 0 && await _probe.IsListeningAsync("127.0.0.1", port, ct).ConfigureAwait(false);
        return new CardServerState(layout.IsInstalled, layout.InstalledVersion(), pids, port, listening);
    }

    public async Task<UpdateCheck> CheckAsync(CardServerLayout? layout, bool includePrereleases, CancellationToken ct = default)
    {
        var installed = layout?.IsInstalled == true ? layout.InstalledVersion() : null;
        try
        {
            var list = await _releases.GetReleasesAsync(ct).ConfigureAwait(false);
            var latest = PackageSelector.Latest(list, includePrereleases);
            var newer = latest is not null && (layout?.IsInstalled != true || ReleaseVersion.IsNewer(installed, latest.Release.Tag));
            return new UpdateCheck(installed, latest, newer, null);
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or System.Text.Json.JsonException or TaskCanceledException)
        {
            return new UpdateCheck(installed, null, false, ex.Message);
        }
    }

    public async Task<int?> StartAsync(CardServerLayout layout, IProgress<string>? log, TimeSpan timeout, CancellationToken ct = default)
    {
        if (!layout.IsInstalled) { log?.Report("No card server is installed in " + layout.ServerDir); return null; }
        var running = _host.FindRunning(layout.ServerDir);
        if (running.Count > 0) { log?.Report($"The card server is already running (pid {running[0]})."); return running[0]; }
        var port = layout.ReadPort();
        var busy = await PortBusyByOtherAsync(layout, port, ct).ConfigureAwait(false);
        if (busy is not null) { log?.Report(busy); return null; }
        var pid = _host.Start(layout.ExePath, layout.ServerDir);
        log?.Report($"Started the card server (pid {pid}); waiting for port {port}...");
        var err = await WaitHealthyAsync(layout, pid, port, timeout, ct).ConfigureAwait(false);
        log?.Report(err ?? $"The card server is listening on port {port}.");
        return err is null ? pid : null;
    }

    public async Task StopAsync(CardServerLayout layout, IProgress<string>? log, TimeSpan grace, CancellationToken ct = default)
    {
        var running = layout.IsInstalled ? _host.FindRunning(layout.ServerDir) : Array.Empty<int>();
        if (running.Count == 0) { log?.Report("The card server is not running."); return; }
        foreach (var pid in running)
        {
            log?.Report($"Stopping the card server (pid {pid})...");
            await _host.StopAsync(pid, grace, ct).ConfigureAwait(false);
        }
        log?.Report("The card server stopped.");
    }

    public async Task<InstallOutcome> InstallAsync(CardServerLayout layout, PackageChoice choice, InstallOptions opts,
        IProgress<string>? log, CancellationToken ct = default)
    {
        var isUpdate = layout.IsInstalled;
        if (isUpdate && !opts.CabsIdleConfirmed)
            return new InstallOutcome(false, false, "Confirm that no cab is in a match or loading first. Nothing was changed.", null);
        if (!isUpdate && Directory.Exists(layout.ServerDir) && Directory.EnumerateFileSystemEntries(layout.ServerDir).Any())
            return new InstallOutcome(false, false, layout.ServerDir + " already has files but no " + CardServerLayout.ExeName +
                                                    ". Pick an empty folder (or the folder of an existing card server).", null);

        // 1. prepare the new build while the current server keeps running
        string newBuild, packageDir;
        string sha;
        try
        {
            (newBuild, packageDir, sha) = await PrepareAsync(layout, choice, log, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new InstallOutcome(false, false, "Nothing was changed: " + ex.Message, null);
        }

        // 2. stop the current server
        var port = layout.ReadPort();
        var wasRunning = isUpdate ? _host.FindRunning(layout.ServerDir) : Array.Empty<int>();
        foreach (var pid in wasRunning)
        {
            log?.Report($"Stopping the card server (pid {pid})...");
            await _host.StopAsync(pid, opts.StopGrace, ct).ConfigureAwait(false);
        }
        var busy = await PortBusyByOtherAsync(layout, port, ct).ConfigureAwait(false);
        if (busy is not null)
        {
            await RestartOldAsync(layout, wasRunning.Count > 0, port, opts, log, ct).ConfigureAwait(false);
            return new InstallOutcome(false, false, busy + " Nothing was changed.", null);
        }

        // 3. swap (backup = the old folder, moved aside whole)
        SwapResult swap;
        try
        {
            log?.Report(isUpdate ? "Backing up the current server folder and swapping in the new build..." : "Installing the new build...");
            swap = CardServerSwap.Apply(newBuild, layout, _now());
            if (swap.Kept.Count > 0) log?.Report("Kept: " + string.Join(", ", swap.Kept.Take(12)) + (swap.Kept.Count > 12 ? ", …" : ""));
            if (swap.BackupDir is not null) log?.Report("Backup of the previous server: " + swap.BackupDir);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            await RestartOldAsync(layout, wasRunning.Count > 0, port, opts, log, ct).ConfigureAwait(false);
            return new InstallOutcome(false, true, "Could not swap the folders (" + ex.Message + "). The previous server was put back.", null);
        }

        // 4. start + health check (always after an update; after a fresh install only when asked)
        if (isUpdate || opts.StartAfterFreshInstall)
        {
            port = layout.ReadPort();
            int newPid;
            string? err;
            try
            {
                newPid = _host.Start(layout.ExePath, layout.ServerDir);
                log?.Report($"Started the new card server (pid {newPid}); waiting up to {opts.HealthTimeout.TotalSeconds:0} s for port {port}...");
                err = await WaitHealthyAsync(layout, newPid, port, opts.HealthTimeout, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                newPid = 0;
                err = "It did not start: " + ex.Message;
            }
            if (err is not null)
            {
                log?.Report(err + " Rolling back...");
                if (newPid > 0) await _host.StopAsync(newPid, opts.StopGrace, ct).ConfigureAwait(false);
                foreach (var pid in _host.FindRunning(layout.ServerDir))
                    await _host.StopAsync(pid, opts.StopGrace, ct).ConfigureAwait(false);
                try { CardServerSwap.Rollback(swap); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    return new InstallOutcome(false, false, err + " Rolling back failed too (" + ex.Message + "). The previous server is in " +
                                                            swap.BackupDir + "; copy it back by hand.", swap.BackupDir);
                }
                foreach (var n in swap.Notes) log?.Report(n);
                await RestartOldAsync(layout, wasRunning.Count > 0, port, opts, log, ct).ConfigureAwait(false);
                return new InstallOutcome(false, true, err + " The previous version was put back" +
                                                       (wasRunning.Count > 0 ? " and restarted." : "."), null);
            }
            log?.Report($"The new card server is listening on port {port}.");
            if (isUpdate && wasRunning.Count == 0)
            {
                log?.Report("It was stopped before the update, so it is stopped again.");
                await _host.StopAsync(newPid, opts.StopGrace, ct).ConfigureAwait(false);
            }
        }

        // 5. bookkeeping
        var previous = isUpdate ? layout.InstalledVersion() : null;
        try
        {
            if (choice.Kind == PackageKind.BuildPackage && (layout.IsPackageRoot || !isUpdate))
                CopyPackageFiles(packageDir, layout.Root);
            new InstallState
            {
                Tag = choice.Release.Tag, Kind = choice.Kind.ToString(), Sha256 = sha, InstalledAt = DateTimeOffset.Now,
                PreviousTag = previous, LastBackup = swap.BackupDir,
            }.Save(layout.StateFile);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            log?.Report("Installed, but could not write the launcher's notes: " + ex.Message);
        }
        CardServerSwap.TryDelete(Path.Combine(layout.StagingDir, SafeName(choice.Release.Tag)));
        foreach (var d in CardServerSwap.PruneBackups(layout)) log?.Report("Removed an old backup: " + d);
        return new InstallOutcome(true, false,
            (isUpdate ? "Updated to " : "Installed ") + choice.Release.Tag + ".", swap.BackupDir);
    }

    // ------------------------------------------------------------------------------------------------ helpers

    private async Task<(string NewBuild, string PackageDir, string Sha)> PrepareAsync(CardServerLayout layout, PackageChoice choice,
        IProgress<string>? log, CancellationToken ct)
    {
        Directory.CreateDirectory(layout.DownloadsDir);
        var zipPath = Path.Combine(layout.DownloadsDir, SafeName(choice.Zip.Name));
        log?.Report($"Downloading {choice.Zip.Name} ({choice.Zip.Size / 1024} KB) from the {choice.Release.Tag} release...");
        await _downloader.DownloadAsync(choice.Zip.DownloadUrl, zipPath, null, ct).ConfigureAwait(false);

        string? fromFile = null;
        if (choice.ShaFile is not null)
            fromFile = Sha256.ParseShaFile(await _downloader.GetStringAsync(choice.ShaFile.DownloadUrl, ct).ConfigureAwait(false), choice.Zip.Name);
        var fromDigest = Sha256.FromDigest(choice.Zip.Digest);
        var actual = await Sha256.ComputeAsync(zipPath, ct).ConfigureAwait(false);
        var bad = Sha256.Verify(actual, fromFile, fromDigest);
        if (bad is not null)
        {
            try { File.Delete(zipPath); } catch (IOException) { }
            throw new InvalidOperationException(bad);
        }
        log?.Report("SHA-256 verified: " + actual);

        var stage = Path.Combine(layout.StagingDir, SafeName(choice.Release.Tag));
        if (Directory.Exists(stage)) Directory.Delete(stage, recursive: true);
        Directory.CreateDirectory(stage);
        ZipFile.ExtractToDirectory(zipPath, stage);   // refuses entries outside the folder

        if (choice.Kind == PackageKind.Prebuilt)
        {
            var exe = Directory.EnumerateFiles(stage, CardServerLayout.ExeName, SearchOption.AllDirectories)
                .OrderBy(p => p.Length).FirstOrDefault()
                ?? throw new InvalidOperationException("The download has no " + CardServerLayout.ExeName + ".");
            return (Path.GetDirectoryName(exe)!, stage, actual);
        }

        var script = Directory.EnumerateFiles(stage, "build-server.ps1", SearchOption.AllDirectories)
            .FirstOrDefault(p => string.Equals(Path.GetFileName(Path.GetDirectoryName(p)), "scripts", StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException("The package has no scripts\\build-server.ps1.");
        var pkg = Path.GetDirectoryName(Path.GetDirectoryName(script)!)!;

        // reuse the private .NET SDK / git the package downloaded last time (tools\), so updates don't fetch them again
        var oldTools = Path.Combine(layout.Root, "tools");
        var pkgTools = Path.Combine(pkg, "tools");
        var movedTools = false;
        if (layout.IsPackageRoot && Directory.Exists(oldTools) && !Directory.Exists(pkgTools))
        {
            try { Directory.Move(oldTools, pkgTools); movedTools = true; }
            catch (IOException) { }
        }
        int code;
        try
        {
            log?.Report("Building the card server with the package's own script (a PowerShell window opens; the first build " +
                        "downloads the card server source and .NET and takes a few minutes)...");
            code = await _builder.RunAsync(pkg, ct).ConfigureAwait(false);
        }
        finally
        {
            // keep the private .NET SDK / git for the next update (they are ~300 MB to download)
            if ((movedTools || layout.IsPackageRoot || !layout.IsInstalled) && Directory.Exists(pkgTools) && !Directory.Exists(oldTools))
                try { Directory.CreateDirectory(layout.Root); Directory.Move(pkgTools, oldTools); } catch (IOException) { }
        }
        var built = Path.Combine(pkg, "server");
        if (code != 0) throw new InvalidOperationException($"The build script failed (exit code {code}). See its window for the reason.");
        if (!File.Exists(Path.Combine(built, CardServerLayout.ExeName)))
            throw new InvalidOperationException("The build finished but " + CardServerLayout.ExeName + " is missing.");
        return (built, pkg, actual);
    }

    private static void CopyPackageFiles(string packageDir, string root)
    {
        if (!Directory.Exists(packageDir)) return;
        Directory.CreateDirectory(root);
        foreach (var f in Directory.EnumerateFiles(packageDir))
            File.Copy(f, Path.Combine(root, Path.GetFileName(f)), overwrite: true);
        foreach (var d in Directory.EnumerateDirectories(packageDir))
        {
            var name = Path.GetFileName(d);
            if (name is "server" or "build" or "tools") continue;
            CardServerSwap.CopyDir(d, Path.Combine(root, name));
        }
    }

    private async Task RestartOldAsync(CardServerLayout layout, bool wasRunning, int port, InstallOptions opts,
        IProgress<string>? log, CancellationToken ct)
    {
        if (!wasRunning || !layout.IsInstalled) return;
        try
        {
            var pid = _host.Start(layout.ExePath, layout.ServerDir);
            var err = await WaitHealthyAsync(layout, pid, layout.ReadPort(), opts.HealthTimeout, ct).ConfigureAwait(false);
            log?.Report(err is null ? $"The previous card server is running again (pid {pid})." : "The previous card server did not come back: " + err);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            log?.Report("Could not restart the previous card server: " + ex.Message);
        }
    }

    private async Task<string?> PortBusyByOtherAsync(CardServerLayout layout, int port, CancellationToken ct)
    {
        if (!await _probe.IsListeningAsync("127.0.0.1", port, ct).ConfigureAwait(false)) return null;
        var owner = _owners?.FindListener(port);
        if (owner?.ExePath is not null && owner.ExePath.StartsWith(layout.ServerDir, StringComparison.OrdinalIgnoreCase)) return null;
        return $"Port {port} is already in use by {owner?.Describe() ?? "another program"}. Stop it or give this card server another port in Configurations\\kestrel.json.";
    }

    internal async Task<string?> WaitHealthyAsync(CardServerLayout layout, int pid, int port, TimeSpan timeout, CancellationToken ct)
    {
        var waited = TimeSpan.Zero;
        var step = TimeSpan.FromSeconds(1);
        while (waited < timeout)
        {
            if (!_host.IsAlive(pid)) return "The card server closed right after starting (check its Logs folder).";
            if (await _probe.IsListeningAsync("127.0.0.1", port, ct).ConfigureAwait(false))
            {
                var owner = _owners?.FindListener(port);
                if (owner is null || owner.Pid == pid ||
                    (owner.ExePath?.StartsWith(layout.ServerDir, StringComparison.OrdinalIgnoreCase) ?? false))
                    return null;
                return $"Port {port} answers, but it belongs to {owner.Describe()}, not the new card server.";
            }
            await _delay.WaitAsync(step, ct).ConfigureAwait(false);
            waited += step;
        }
        return $"The card server did not listen on port {port} within {timeout.TotalSeconds:0} s.";
    }

    private static string SafeName(string s) =>
        string.Concat(s.Select(c => char.IsLetterOrDigit(c) || c is '.' or '-' or '_' ? c : '_'));
}

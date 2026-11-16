using Exvs2Launcher.Core.Launch;

namespace Exvs2Launcher.Core.VirtualLan;

public enum VirtualLanState
{
    Off,
    Starting,
    WaitingForAdapter,
    Up,
    Stopping,
    Failed,
}

/// <summary>Starts edge.exe. On Windows this is elevated (UAC); the launcher itself stays asInvoker.</summary>
public interface IEdgeHost
{
    /// <summary>Starts edge with <paramref name="command"/>; output goes to <paramref name="logPath"/>.</summary>
    IStartedProcess Start(EdgeCommand command, string logPath);

    /// <summary>
    /// Last resort when edge ignores the management "stop": an elevated taskkill (one more UAC prompt).
    /// Returns a note for the log.
    /// </summary>
    string ForceStop(IStartedProcess process);
}

/// <summary>Private files edge needs: the short-lived config (contains the key) and the output log.</summary>
public interface IEdgeFiles
{
    void WritePrivate(string path, string text);
    void Delete(string path);
    /// <summary>Lines appended to <paramref name="path"/> since the last call (empty if none or unreadable).</summary>
    IReadOnlyList<string> ReadNewLines(string path);
    /// <summary>Deletes an old log and forgets how much of it was read.</summary>
    void ResetLog(string path);
}

public sealed class SystemEdgeFiles : IEdgeFiles
{
    private readonly Dictionary<string, long> _offsets = new(StringComparer.OrdinalIgnoreCase);

    public void WritePrivate(string path, string text)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        File.WriteAllText(path, text);
    }

    public void Delete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    public IReadOnlyList<string> ReadNewLines(string path)
    {
        try
        {
            if (!File.Exists(path)) return Array.Empty<string>();
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            _offsets.TryGetValue(path, out long offset);
            if (offset > fs.Length) offset = 0;
            fs.Seek(offset, SeekOrigin.Begin);
            using var sr = new StreamReader(fs);
            var text = sr.ReadToEnd();
            int lastNewline = text.LastIndexOf('\n');
            if (lastNewline < 0) return Array.Empty<string>();
            var complete = text[..(lastNewline + 1)];
            _offsets[path] = offset + System.Text.Encoding.UTF8.GetByteCount(complete);
            return complete.Split('\n').Select(l => l.TrimEnd('\r')).Where(l => l.Length > 0).ToList();
        }
        catch (IOException) { return Array.Empty<string>(); }
        catch (UnauthorizedAccessException) { return Array.Empty<string>(); }
    }

    public void ResetLog(string path)
    {
        _offsets.Remove(path);
        Delete(path);
    }
}

public sealed record VirtualLanResult(bool Succeeded, string? Ip, string Message);

public sealed record VirtualLanStatus(VirtualLanState State, string? Ip, string? Adapter, EdgePeers Peers, bool StartedByLauncher, int? Pid, string? LastError)
{
    public string Describe() => State switch
    {
        VirtualLanState.Off => "Virtual LAN: off",
        VirtualLanState.Starting => "Virtual LAN: starting edge...",
        VirtualLanState.WaitingForAdapter => "Virtual LAN: waiting for the TAP adapter...",
        VirtualLanState.Up => $"Virtual LAN: up, {Ip} on {Adapter ?? "TAP"}, {Peers.Describe()}" + (StartedByLauncher ? $" (edge pid {Pid})" : " (edge started outside the launcher)"),
        VirtualLanState.Stopping => "Virtual LAN: stopping edge...",
        _ => "Virtual LAN: failed" + (LastError is null ? "" : " - " + LastError),
    };
}

/// <summary>
/// Owns the n2n edge process for virtual LAN mode: start (elevated) → wait for the TAP IPv4 → Up, and a graceful
/// stop through edge's management port so the supernode releases the MAC/IP immediately. The key is only written
/// to a private config file that is deleted as soon as edge has read it; nothing logged contains the key or
/// community name.
/// </summary>
public sealed class VirtualLanController
{
    private readonly IEdgeHost _host;
    private readonly IAdapterSource _adapters;
    private readonly IEdgeManagement _mgmt;
    private readonly IDelay _delay;
    private readonly IEdgeFiles _files;
    private readonly string _workDir;
    private readonly Func<string, bool> _fileExists;
    private IStartedProcess? _edge;
    private VirtualLanSettings? _settings;
    private string? _key;
    private string? _community;

    public VirtualLanController(IEdgeHost host, IAdapterSource adapters, IEdgeManagement mgmt, IDelay delay,
        string workDir, IEdgeFiles? files = null, Func<string, bool>? fileExists = null)
    {
        _host = host;
        _adapters = adapters;
        _mgmt = mgmt;
        _delay = delay;
        _workDir = workDir;
        _files = files ?? new SystemEdgeFiles();
        _fileExists = fileExists ?? File.Exists;
    }

    public TimeSpan PollInterval { get; init; } = TimeSpan.FromMilliseconds(500);
    public TimeSpan StopTimeout { get; init; } = TimeSpan.FromSeconds(5);

    public VirtualLanState State { get; private set; } = VirtualLanState.Off;
    public string? Ip { get; private set; }
    public string? AdapterName { get; private set; }
    public EdgePeers Peers { get; private set; } = EdgePeers.Unknown;
    public string? LastError { get; private set; }
    public bool StartedByLauncher => _edge is not null;
    public int? EdgePid => _edge?.Id;

    public string ConfigPath => Path.Combine(_workDir, "edge.conf");
    public string LogPath => Path.Combine(_workDir, "edge.log");

    public event Action<VirtualLanState>? StateChanged;
    public event Action<string>? Log;

    public VirtualLanStatus Status => new(State, Ip, AdapterName, Peers, StartedByLauncher, EdgePid, LastError);

    public bool IsUp => State == VirtualLanState.Up;

    public async Task<VirtualLanResult> StartAsync(VirtualLanSettings settings, string? key, CancellationToken ct = default)
    {
        if (State is VirtualLanState.Starting or VirtualLanState.WaitingForAdapter or VirtualLanState.Stopping)
            return new VirtualLanResult(false, null, "The virtual LAN is already starting or stopping.");

        _settings = settings.Clone();
        _key = key;
        _community = settings.Community?.Trim();

        if (State == VirtualLanState.Up)
        {
            if (_edge is not null && _edge.HasExited)
            {
                Say($"edge (pid {_edge.Id}) has exited (code {_edge.ExitCode?.ToString() ?? "?"}). Starting it again.");
                _edge = null;
                Enter(VirtualLanState.Off);
            }
            else
            {
                var again = TapAdapters.Select(_adapters.List(), settings);
                if (again.Ip is not null)
                {
                    Ip = again.Ip;
                    AdapterName = again.Adapter?.Name;
                    return new VirtualLanResult(true, Ip, $"Virtual LAN already up: {Ip} on {AdapterName}.");
                }
                Say("The TAP adapter lost its address. Restarting edge.");
                await StopAsync(ct).ConfigureAwait(false);
            }
        }

        Enter(VirtualLanState.Starting);
        LastError = null;
        Ip = null;
        Peers = EdgePeers.Unknown;

        var errors = VirtualLanConfig.Validate(settings, key, _fileExists);
        if (errors.Count > 0) return Fail(string.Join(" ", errors));

        var first = TapAdapters.Select(_adapters.List(), settings);
        if (first.Adapter is null) return Fail(first.Problem ?? TapAdapters.MissingDriverMessage);

        // An edge started earlier (by another launcher session or by hand) may already own the TAP.
        if (first.Ip is not null)
        {
            EdgePeers answered;
            try { answered = await _mgmt.QueryPeersAsync(settings.ManagementPort, ct).ConfigureAwait(false); }
            catch (Exception) { answered = EdgePeers.Unknown; }
            if (answered.Parsed)
            {
                Ip = first.Ip;
                AdapterName = first.Adapter.Name;
                Peers = answered;
                Enter(VirtualLanState.Up);
                Say($"An edge is already running on management port {settings.ManagementPort} and the TAP has {Ip}. Using it; the launcher will not stop an edge it did not start.");
                return new VirtualLanResult(true, Ip, "Using the edge that is already running.");
            }
        }

        EdgeCommand command;
        try { command = EdgeCommand.Build(settings, key!, ConfigPath); }
        catch (ArgumentException ex) { return Fail(ex.Message); }

        try
        {
            _files.ResetLog(LogPath);
            _files.WritePrivate(ConfigPath, command.ConfigFileText());
            Say("Starting n2n edge (Windows will ask for administrator permission): " + command.DisplayCommandLine);
            Say("edge options: " + command.RedactedArgumentString());
            try { _edge = _host.Start(command, LogPath); }
            catch (Exception ex)
            {
                return Fail("Could not start edge.exe: " + ex.Message +
                            " If you clicked No on the Windows permission prompt, start again and allow it; edge needs administrator rights to open the TAP adapter.");
            }

            Enter(VirtualLanState.WaitingForAdapter);
            var timeout = TimeSpan.FromSeconds(settings.StartTimeoutSeconds);
            Say($"Waiting for the TAP adapter to get its IPv4 (up to {timeout.TotalSeconds:0} s)...");
            int polls = Math.Max(1, (int)Math.Ceiling(timeout / PollInterval));
            string? lastProblem = null;
            for (int i = 0; i < polls; i++)
            {
                ct.ThrowIfCancellationRequested();
                PumpLog();
                if (_edge.HasExited)
                {
                    var code = _edge.ExitCode?.ToString() ?? "?";
                    _edge = null;
                    return Fail($"edge.exe exited (code {code}) before the TAP adapter came up. " +
                                "Check the edge lines above, that the TAP-Windows driver is installed, the supernode address, community, and key, " +
                                "and that TinyWall / Windows Firewall allows edge.exe.");
                }
                var sel = TapAdapters.Select(_adapters.List(), settings);
                if (sel.Ip is not null)
                {
                    Ip = sel.Ip;
                    AdapterName = sel.Adapter?.Name;
                    _files.Delete(ConfigPath);
                    Enter(VirtualLanState.Up);
                    Say($"Virtual LAN up: {Ip} on {AdapterName}. The card server stays on this PC (127.0.0.1).");
                    return new VirtualLanResult(true, Ip, "Virtual LAN up.");
                }
                lastProblem = sel.Problem;
                await _delay.WaitAsync(PollInterval, ct).ConfigureAwait(false);
            }

            PumpLog();
            await StopOwnedEdgeAsync(CancellationToken.None).ConfigureAwait(false);
            return Fail($"The TAP adapter did not get a virtual IP within {timeout.TotalSeconds:0} s ({lastProblem ?? "no address"}). " +
                        "edge was stopped. Check that the supernode is reachable (UDP), TinyWall allows edge.exe, and the community and key match the other players" +
                        (settings.IpMode == VirtualIpMode.Dhcp ? ", and that someone on the virtual LAN runs DHCP." : "."));
        }
        catch (OperationCanceledException)
        {
            await StopOwnedEdgeAsync(CancellationToken.None).ConfigureAwait(false);
            Ip = null;
            Enter(VirtualLanState.Off);
            Say("Virtual LAN start cancelled; edge was stopped.");
            return new VirtualLanResult(false, null, "Cancelled.");
        }
        finally
        {
            _files.Delete(ConfigPath);
        }
    }

    /// <summary>Stops the edge this launcher started (graceful first). An edge started elsewhere is left alone.</summary>
    public async Task<string> StopAsync(CancellationToken ct = default)
    {
        if (_edge is null)
        {
            bool wasUp = State == VirtualLanState.Up;
            Ip = null;
            Peers = EdgePeers.Unknown;
            Enter(VirtualLanState.Off);
            var msg = wasUp ? "The virtual LAN edge was not started by this launcher; it was left running." : "The virtual LAN is not running.";
            Say(msg);
            return msg;
        }
        Enter(VirtualLanState.Stopping);
        var note = await StopOwnedEdgeAsync(ct).ConfigureAwait(false);
        Ip = null;
        Peers = EdgePeers.Unknown;
        Enter(VirtualLanState.Off);
        return note;
    }

    /// <summary>Refreshes peers and notices an edge that exited on its own. Call every few seconds while Up.</summary>
    public async Task<VirtualLanStatus> RefreshAsync(CancellationToken ct = default)
    {
        PumpLog();
        if (State != VirtualLanState.Up || _settings is null) return Status;
        if (_edge is not null && _edge.HasExited)
        {
            var code = _edge.ExitCode?.ToString() ?? "?";
            _edge = null;
            Ip = null;
            LastError = $"edge.exe exited (code {code}). The LM and game lost the virtual LAN.";
            Enter(VirtualLanState.Failed);
            Say(LastError);
            return Status;
        }
        try { Peers = await _mgmt.QueryPeersAsync(_settings.ManagementPort, ct).ConfigureAwait(false); }
        catch (Exception) { Peers = EdgePeers.Unknown; }
        var sel = TapAdapters.Select(_adapters.List(), _settings);
        if (sel.Ip is not null) { Ip = sel.Ip; AdapterName = sel.Adapter?.Name; }
        return Status;
    }

    private async Task<string> StopOwnedEdgeAsync(CancellationToken ct)
    {
        var edge = _edge;
        if (edge is null) return "edge is not running.";
        if (edge.HasExited) { _edge = null; return "edge had already exited."; }
        int port = _settings?.ManagementPort ?? VirtualLanSettings.DefaultManagementPort;
        try { await _mgmt.RequestStopAsync(port, ct).ConfigureAwait(false); }
        catch (Exception ex) { Say("Could not send stop to edge's management port: " + ex.Message); }

        int polls = Math.Max(1, (int)Math.Ceiling(StopTimeout / PollInterval));
        for (int i = 0; i < polls && !edge.HasExited; i++)
            await _delay.WaitAsync(PollInterval, ct).ConfigureAwait(false);
        PumpLog();

        string note;
        if (edge.HasExited) note = $"Stopped n2n edge (pid {edge.Id}) cleanly.";
        else
        {
            note = $"edge (pid {edge.Id}) did not stop within {StopTimeout.TotalSeconds:0} s. " + _host.ForceStop(edge);
        }
        _edge = null;
        Say(note);
        return note;
    }

    private void PumpLog()
    {
        foreach (var line in _files.ReadNewLines(LogPath))
            Say("edge: " + EdgeCommand.Redact(line, _key, _community));
    }

    private VirtualLanResult Fail(string message)
    {
        LastError = EdgeCommand.Redact(message, _key, _community);
        Ip = null;
        Enter(VirtualLanState.Failed);
        Say("Virtual LAN: " + LastError);
        return new VirtualLanResult(false, null, LastError);
    }

    private void Enter(VirtualLanState s)
    {
        if (State == s) return;
        State = s;
        StateChanged?.Invoke(s);
    }

    private void Say(string m) => Log?.Invoke(EdgeCommand.Redact(m, _key, _community));
}

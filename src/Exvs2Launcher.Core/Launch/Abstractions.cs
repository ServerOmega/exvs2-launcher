namespace Exvs2Launcher.Core.Launch;

public sealed record LaunchSpec(string Role, string FileName, string Arguments, string WorkingDirectory);

public interface IStartedProcess
{
    string Role { get; }
    int Id { get; }
    bool HasExited { get; }
    int? ExitCode { get; }
    /// <summary>Kills the process and its children (a .bat window and the server it started).</summary>
    void Kill();
}

public interface IProcessStarter
{
    IStartedProcess Start(LaunchSpec spec);
}

public interface IPortProbe
{
    Task<bool> IsListeningAsync(string host, int port, CancellationToken ct);
}

/// <summary>The process that owns a listening TCP port.</summary>
public sealed record PortOwner(int Pid, string? ProcessName, string? ExePath)
{
    public string Describe() => $"{ProcessName ?? "unknown"} (pid {Pid}{(ExePath is null ? "" : ", " + ExePath)})";
}

public interface IPortOwnerLookup
{
    /// <summary>Returns the owner of a TCP listener on <paramref name="port"/>, or null if none or unknown.</summary>
    PortOwner? FindListener(int port);
}

public interface IDelay
{
    Task WaitAsync(TimeSpan duration, CancellationToken ct);
}

public sealed class RealDelay : IDelay
{
    public Task WaitAsync(TimeSpan duration, CancellationToken ct) => Task.Delay(duration, ct);
}

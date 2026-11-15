namespace Exvs2Launcher.Core.Launch;

public sealed class StartPlan
{
    public required string GameFolder { get; init; }
    /// <summary>The patched card server to start: Server-Infinite.exe, or a .bat/.cmd such as 3-Start-Server.bat.</summary>
    public string? CardServerPath { get; init; }
    public string CardServerArguments { get; init; } = "";
    public bool StartCardServer { get; init; } = true;
    public bool StartLm { get; init; }
    public bool StartClient { get; init; } = true;
    public string Host { get; init; } = "127.0.0.1";
    public int Port { get; init; } = 28080;
    public TimeSpan PortTimeout { get; init; } = TimeSpan.FromSeconds(90);
    public TimeSpan PollInterval { get; init; } = TimeSpan.FromMilliseconds(500);
    public TimeSpan LmToClientDelay { get; init; } = TimeSpan.FromSeconds(3);
    /// <summary>If our patched server already holds the port, use it instead of failing.</summary>
    public bool ReuseRunningPatchedServer { get; init; } = true;
}

public enum StartStage
{
    Idle,
    Validating,
    CheckingPort,
    StartingCardServer,
    WaitingForPort,
    StartingLm,
    WaitingBeforeClient,
    StartingClient,
    Running,
    Failed,
    Cancelled,
}

public sealed record StartOutcome(StartStage FinalStage, string Message, IReadOnlyList<IStartedProcess> Started)
{
    public bool Succeeded => FinalStage == StartStage.Running;
}

public enum PortOwnerKind { None, PatchedServer, StockServer, Other, Unknown }

public static class PortOwnerClassifier
{
    public const string PatchedServerProcessName = "Server-Infinite";

    /// <summary>
    /// Decides whether a listener is our patched server, the pack's stock <c>server\Server.exe</c>, or something else.
    /// </summary>
    public static PortOwnerKind Classify(bool listening, PortOwner? owner, StartPlan plan)
    {
        if (!listening && owner is null) return PortOwnerKind.None;
        if (owner is null || owner.ProcessName is null) return PortOwnerKind.Unknown;

        if (owner.ExePath is not null && plan.CardServerPath is not null &&
            IsExe(plan.CardServerPath) && SamePath(owner.ExePath, plan.CardServerPath))
            return PortOwnerKind.PatchedServer;

        if (string.Equals(owner.ProcessName, PatchedServerProcessName, StringComparison.OrdinalIgnoreCase))
            return PortOwnerKind.PatchedServer;

        if (string.Equals(owner.ProcessName, "Server", StringComparison.OrdinalIgnoreCase))
            return PortOwnerKind.StockServer;

        return PortOwnerKind.Other;
    }

    public static bool IsExe(string path) => path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase);

    public static bool SamePath(string a, string b)
    {
        try { return string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase); }
        catch (Exception) { return string.Equals(a, b, StringComparison.OrdinalIgnoreCase); }
    }
}

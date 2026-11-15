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
    /// <summary>
    /// When false, starting only the LM or client does not fail just because the card server port is free.
    /// "Start all" leaves this true so a missing card server is still an error.
    /// </summary>
    public bool RequireCardServerPort { get; init; } = true;
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
    /// A process named <c>Server</c> is not assumed to be stock: hidden patched builds (for example mm-server2)
    /// are often a renamed <c>Server.exe</c> sitting next to a <c>BUILD_COMMIT</c> file.
    /// </summary>
    public static PortOwnerKind Classify(bool listening, PortOwner? owner, StartPlan plan, Func<string, bool>? fileExists = null)
    {
        fileExists ??= File.Exists;
        if (!listening && owner is null) return PortOwnerKind.None;
        if (owner is null || owner.ProcessName is null) return PortOwnerKind.Unknown;

        if (LooksPatched(owner, plan, fileExists)) return PortOwnerKind.PatchedServer;
        if (LooksStock(owner, plan, fileExists)) return PortOwnerKind.StockServer;
        // A hidden patched build is often just named "Server". Without an exe path we cannot
        // tell it from the pack's stock server, so do not call it stock.
        if (string.Equals(owner.ProcessName, "Server", StringComparison.OrdinalIgnoreCase) &&
            string.IsNullOrWhiteSpace(owner.ExePath))
            return PortOwnerKind.Unknown;
        return PortOwnerKind.Other;
    }

    public static bool LooksPatched(PortOwner owner, StartPlan plan, Func<string, bool>? fileExists = null)
    {
        fileExists ??= File.Exists;
        if (owner.ExePath is not null && plan.CardServerPath is not null &&
            IsExe(plan.CardServerPath) && SamePath(owner.ExePath, plan.CardServerPath))
            return true;

        if (string.Equals(owner.ProcessName, PatchedServerProcessName, StringComparison.OrdinalIgnoreCase))
            return true;

        if (!string.IsNullOrWhiteSpace(owner.ExePath) &&
            Path.GetFileName(owner.ExePath).Equals("Server-Infinite.exe", StringComparison.OrdinalIgnoreCase))
            return true;

        return HasPatchedMarker(owner.ExePath, fileExists);
    }

    public static bool LooksStock(PortOwner owner, StartPlan plan, Func<string, bool>? fileExists = null)
    {
        fileExists ??= File.Exists;
        if (HasPatchedMarker(owner.ExePath, fileExists)) return false;

        if (!string.IsNullOrWhiteSpace(plan.GameFolder) && !string.IsNullOrWhiteSpace(owner.ExePath))
        {
            try
            {
                var stock = Path.Combine(Path.GetFullPath(plan.GameFolder), "server", "Server.exe");
                if (SamePath(owner.ExePath, stock)) return true;
            }
            catch (Exception) { }
        }

        return IsStockPackLayout(owner.ExePath);
    }

    /// <summary>
    /// True when the exe folder (or a parent a few levels up) has BUILD_COMMIT / VERSION, or the folder name
    /// looks like a matchmaking package (mm-server, exvs2-matchmaking).
    /// </summary>
    public static bool HasPatchedMarker(string? exePath, Func<string, bool>? fileExists = null)
    {
        if (string.IsNullOrWhiteSpace(exePath)) return false;
        fileExists ??= File.Exists;
        string? dir;
        try { dir = Path.GetDirectoryName(Path.GetFullPath(exePath.Trim())); }
        catch (Exception) { return false; }

        for (int i = 0; i < 5 && !string.IsNullOrEmpty(dir); i++)
        {
            if (fileExists(Path.Combine(dir, "BUILD_COMMIT")) ||
                fileExists(Path.Combine(dir, "VERSION")) ||
                fileExists(Path.Combine(dir, "version.txt")))
                return true;

            var name = Path.GetFileName(dir);
            if (!string.IsNullOrEmpty(name) &&
                (name.StartsWith("mm-server", StringComparison.OrdinalIgnoreCase) ||
                 name.Contains("exvs2-matchmaking", StringComparison.OrdinalIgnoreCase)))
                return true;

            try { dir = Directory.GetParent(dir)?.FullName; }
            catch (Exception) { break; }
        }
        return false;
    }

    public static bool IsStockPackLayout(string? exePath)
    {
        if (string.IsNullOrWhiteSpace(exePath)) return false;
        // Split on both slashes so Windows paths still classify correctly when tests run on Linux.
        var parts = exePath.Trim().Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length >= 2
               && parts[^1].Equals("Server.exe", StringComparison.OrdinalIgnoreCase)
               && parts[^2].Equals("server", StringComparison.OrdinalIgnoreCase);
    }

    public static string Describe(PortOwnerKind kind, int port, PortOwner? owner)
    {
        string who = owner?.Describe() ?? "an unidentified program";
        string pathAndPid = owner is null
            ? who
            : (string.IsNullOrWhiteSpace(owner.ExePath) ? who : $"{owner.ExePath} (pid {owner.Pid})");

        return kind switch
        {
            PortOwnerKind.None =>
                $"Port {port} is free. Start will launch the card server first.",
            PortOwnerKind.PatchedServer =>
                $"Port {port} is used by the patched card server at {pathAndPid}. Untick Card server to use it, or stop it first.",
            PortOwnerKind.StockServer =>
                $"Port {port} is held by the stock card server {who}. Close it (or SnowLauncher) first; the game would use the stock server instead of the patched one.",
            PortOwnerKind.Other =>
                $"Port {port} is held by {who}, which is not the patched card server. Close it and try again.",
            _ =>
                $"Port {port} is in use" + (owner is null ? ", but the launcher could not tell which program." : $" by {who}, and the launcher could not tell whether it is the pack's stock server or a renamed patched build. If this is a patched server (look for a BUILD_COMMIT file next to the exe), untick Card server and start the LM and client, or close it and try again."),
        };
    }

    public static bool IsExe(string path) => path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase);

    public static bool SamePath(string a, string b)
    {
        try { return string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase); }
        catch (Exception) { return string.Equals(a, b, StringComparison.OrdinalIgnoreCase); }
    }
}

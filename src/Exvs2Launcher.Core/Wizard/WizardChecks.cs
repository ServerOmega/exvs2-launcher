using Exvs2Launcher.Core.Files;
using Exvs2Launcher.Core.Launch;

namespace Exvs2Launcher.Core.Wizard;

public sealed record CheckResult(string Name, bool Ok, string Message);

public sealed record FirewallReport(
    bool TinyWallLikely,
    bool WindowsFirewallLikely,
    IReadOnlyList<string> Evidence,
    IReadOnlyList<string> ExesToAllow);

public interface IEnvironmentProbe
{
    bool FileExists(string path);
    bool ProcessNameRunning(string name);
    bool ServiceRunning(string serviceName);
    IReadOnlyList<string> ProgramFilesRoots();
}

/// <summary>Pre-flight checks for the wizard. Never changes firewall rules, and never kills a process it did not start.</summary>
public static class WizardChecks
{
    public static IReadOnlyList<string> ExesToAllow(string? gameFolder, string? cardServerPath)
    {
        var list = new List<string>();
        if (!string.IsNullOrWhiteSpace(gameFolder))
        {
            try
            {
                var g = new GameFolder(gameFolder);
                list.Add(g.ClientExe);
                list.Add(g.LmExe);
            }
            catch (Exception) { }
        }
        var patched = CardServerConfig.FindPatchedExe(cardServerPath);
        if (patched is not null) list.Add(patched);
        else if (!string.IsNullOrWhiteSpace(cardServerPath) &&
                 cardServerPath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            list.Add(cardServerPath);
        return list.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    public static FirewallReport DetectFirewall(IEnvironmentProbe probe, string? gameFolder, string? cardServerPath)
    {
        var evidence = new List<string>();
        bool tiny = false;
        foreach (var root in probe.ProgramFilesRoots())
        {
            var exe = Path.Combine(root, "TinyWall", "TinyWall.exe");
            if (probe.FileExists(exe))
            {
                tiny = true;
                evidence.Add("Found " + exe);
            }
        }
        if (probe.ProcessNameRunning("TinyWall") || probe.ProcessNameRunning("TinyWallService"))
        {
            tiny = true;
            evidence.Add("A TinyWall process is running.");
        }

        bool winFw = probe.ServiceRunning("mpssvc") || probe.ServiceRunning("MpsSvc");
        if (winFw) evidence.Add("The Windows Firewall service (mpssvc) is running.");

        if (!tiny && !winFw && evidence.Count == 0)
            evidence.Add("No TinyWall install or Windows Firewall service was detected. A different firewall may still be present.");

        return new FirewallReport(tiny, winFw, evidence, ExesToAllow(gameFolder, cardServerPath));
    }

    public static string FirewallAdvice(FirewallReport report)
    {
        var lines = new List<string>();
        if (report.TinyWallLikely)
            lines.Add("TinyWall appears to be installed. The launcher will not change it. Add the programs below to the TinyWall allow list.");
        if (report.WindowsFirewallLikely)
            lines.Add("Windows Firewall is running. The launcher will not change it. Allow the programs below if they are blocked.");
        if (!report.TinyWallLikely && !report.WindowsFirewallLikely)
            lines.Add("No TinyWall or Windows Firewall service was detected. If a different firewall is in use, allow the same programs.");
        if (report.ExesToAllow.Count > 0)
        {
            lines.Add("Allow these exact programs:");
            lines.AddRange(report.ExesToAllow.Select(e => "  " + e));
        }
        return string.Join(Environment.NewLine, lines);
    }

    /// <summary>
    /// HTTP GET to the matchmaker URL. Any HTTP response (including 4xx) means the host is reachable.
    /// The URL is sanitized in the message; secrets are never included.
    /// </summary>
    public static async Task<CheckResult> MatchmakerHttpAsync(
        string? matchUrl, HttpMessageHandler? handler = null, TimeSpan? timeout = null, CancellationToken ct = default)
    {
        if (!CardServerConfig.TryNormalizeMatchUrl(matchUrl, out var url, out _, out var err))
            return new CheckResult("Matchmaker", false, err ?? "Enter a matchmaker address first.");

        var safe = SafeUrl(url!);
        try
        {
            using var client = handler is null ? new HttpClient() : new HttpClient(handler, disposeHandler: false);
            client.Timeout = timeout ?? TimeSpan.FromSeconds(5);
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            using var resp = await client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            return new CheckResult("Matchmaker", true,
                "Matchmaker answered HTTP " + (int)resp.StatusCode + " at " + safe + ".");
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return new CheckResult("Matchmaker", false, "Timed out waiting for the matchmaker at " + safe + ".");
        }
        catch (Exception ex)
        {
            return new CheckResult("Matchmaker", false, "Could not reach the matchmaker at " + safe + ": " + Unwrap(ex));
        }
    }

    /// <summary>
    /// Probes the card-server port. If it is already held by a patched server, that instance is reused and
    /// is never stopped. If the port is free, the server is started, the port is checked, and only the
    /// process this method started is stopped.
    /// </summary>
    public static async Task<CheckResult> CardServerPortAsync(
        WizardState state,
        IPortProbe probe,
        IPortOwnerLookup owners,
        IProcessStarter? starter = null,
        IDelay? delay = null,
        TimeSpan? timeout = null,
        CancellationToken ct = default)
    {
        int port = state.CardServerPort <= 0 ? 28080 : state.CardServerPort;
        var plan = new StartPlan
        {
            GameFolder = state.GameFolder ?? "",
            CardServerPath = state.CardServerPath,
            Port = port,
        };

        bool listening = await probe.IsListeningAsync("127.0.0.1", port, ct).ConfigureAwait(false);
        var owner = owners.FindListener(port);
        var kind = PortOwnerClassifier.Classify(listening, owner, plan);

        if (kind == PortOwnerKind.PatchedServer)
            return new CheckResult("Card server port", true,
                "Port " + port + " already answers and is held by the patched card server" +
                (owner is null ? "." : " (" + owner.Describe() + ").") + " It was left running.");
        if (kind == PortOwnerKind.StockServer)
            return new CheckResult("Card server port", false,
                "Port " + port + " is held by the pack's stock Server.exe. Stop that and point the launcher at Server-Infinite.exe.");
        if (kind == PortOwnerKind.Other || (listening && kind == PortOwnerKind.Unknown))
            return new CheckResult("Card server port", false,
                "Port " + port + " is already in use by " + (owner?.Describe() ?? "an unknown process") +
                ". That process was not stopped.");

        if (string.IsNullOrWhiteSpace(state.CardServerPath) || !File.Exists(state.CardServerPath))
            return new CheckResult("Card server port", false,
                "Port " + port + " is free, but no card server path was set, so a test start was not attempted.");
        if (starter is null)
            return new CheckResult("Card server port", false,
                "Port " + port + " is free. A test start of the card server was not run in this context.");

        IStartedProcess? started = null;
        try
        {
            var workDir = Path.GetDirectoryName(Path.GetFullPath(state.CardServerPath)) ?? "";
            started = starter.Start(new LaunchSpec("Card server", state.CardServerPath, "", workDir));
            delay ??= new RealDelay();
            var until = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(15));
            while (DateTime.UtcNow < until)
            {
                ct.ThrowIfCancellationRequested();
                if (await probe.IsListeningAsync("127.0.0.1", port, ct).ConfigureAwait(false))
                {
                    return new CheckResult("Card server port", true,
                        "Test-started the card server (pid " + started.Id + "); port " + port + " answered. The test process was then stopped.");
                }
                await delay.WaitAsync(TimeSpan.FromMilliseconds(200), ct).ConfigureAwait(false);
            }
            return new CheckResult("Card server port", false,
                "Test-started the card server (pid " + started.Id + "), but port " + port + " did not answer in time. The test process was stopped.");
        }
        catch (Exception ex)
        {
            return new CheckResult("Card server port", false, "Could not test-start the card server: " + Unwrap(ex));
        }
        finally
        {
            try { started?.Kill(); }
            catch (Exception) { }
        }
    }

    public static string SafeUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return url;
        var builder = new UriBuilder(uri) { UserName = "", Password = "" };
        return builder.Uri.ToString();
    }

    private static string Unwrap(Exception ex)
    {
        var cur = ex;
        while (cur.InnerException is not null) cur = cur.InnerException;
        return cur.Message;
    }
}

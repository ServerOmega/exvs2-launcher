using Exvs2Launcher.Core.Launch;
using Exvs2Launcher.Core.VirtualLan;

namespace Exvs2Launcher.Core.Profiles;

/// <summary>
/// One cab (one game folder). Card values are deliberately not stored here: they live only in the game folder's
/// card.ini, so this file is safe to look at or share.
/// </summary>
public sealed class LauncherProfile
{
    public string Name { get; set; } = "New cab";
    public string GameFolder { get; set; } = "";
    public string? CardServerPath { get; set; }
    public string CardServerArguments { get; set; } = "";

    public bool StartCardServer { get; set; } = true;
    public bool StartLm { get; set; }
    public bool StartClient { get; set; } = true;

    public string CardServerHost { get; set; } = "127.0.0.1";
    public int CardServerPort { get; set; } = 28080;
    public int PortTimeoutSeconds { get; set; } = 90;
    public int LmToClientDelaySeconds { get; set; } = 3;

    /// <summary>Detect this PC's LAN IP on every start and write it as the PCB (client) IP only. The LM IP is never auto-filled; lmnfig.ini is authoritative.</summary>
    public bool AutoDetectIp { get; set; } = true;
    public string? PcbIp { get; set; }
    public string? LmIp { get; set; }

    /// <summary>Also update SnowLauncher.ini (if present) so opening SnowLauncher later does not restore old values.</summary>
    public bool SyncSnowLauncherIni { get; set; } = true;

    /// <summary>Digits kept at the start of new access codes, for example "99". Empty = fully random.</summary>
    public string AccessCodePrefix { get; set; } = "";

    /// <summary>The serial last written by the launcher (not secret; used to keep serials unique across cabs).</summary>
    public string? Serial { get; set; }

    /// <summary>
    /// Last observed game client window title (for example PCB_03). Informational only — the game sets this
    /// itself, and the launcher cannot rename it. Shown next to the cab name so PCB_xx windows are easier to match.
    /// </summary>
    public string? LastGameWindowTitle { get; set; }

    /// <summary>
    /// Optional n2n virtual LAN mode (off by default). The key is not stored here; see VirtualLanKeyStore.
    /// </summary>
    public VirtualLanSettings VirtualLan { get; set; } = new();

    public StartPlan ToStartPlan() => new()
    {
        GameFolder = GameFolder,
        CardServerPath = CardServerPath,
        CardServerArguments = CardServerArguments,
        StartCardServer = StartCardServer,
        StartLm = StartLm,
        StartClient = StartClient,
        Host = CardServerHost,
        Port = CardServerPort,
        PortTimeout = TimeSpan.FromSeconds(Math.Max(5, PortTimeoutSeconds)),
        LmToClientDelay = TimeSpan.FromSeconds(Math.Max(0, LmToClientDelaySeconds)),
    };

    public string ServerAddress => $"{CardServerHost}:{CardServerPort}";
}

namespace Exvs2Launcher.Core.Profiles;

/// <summary>
/// Launcher-wide settings saved in the profile file. Every network-facing or attention-grabbing feature is off by
/// default except the startup update check, which only notifies.
/// </summary>
public sealed class LauncherSettings
{
    // ---- Start everything ----
    /// <summary>Seconds to wait after the LM(s) start before the first client.</summary>
    public int LmToClientsDelaySeconds { get; set; } = 5;
    /// <summary>Seconds between one client and the next.</summary>
    public int ClientStaggerSeconds { get; set; } = 8;

    // ---- Self-update ----
    public bool CheckForUpdatesOnStartup { get; set; } = true;
    public bool UpdateIncludePrereleases { get; set; } = true;
    /// <summary>A release tag the user chose to skip (no startup notice for it).</summary>
    public string? SkippedUpdateTag { get; set; }

    // ---- Discord Rich Presence ----
    public bool RichPresenceEnabled { get; set; }
    /// <summary>The Discord application id (a number from the Discord developer portal). Blank = Rich Presence off.</summary>
    public string RichPresenceClientId { get; set; } = "";
    public bool RichPresenceShowInviteButton { get; set; } = true;

    // ---- Notifications ----
    public bool NotifySound { get; set; }
    public bool NotifyToast { get; set; }
    /// <summary>Notify when the selected matchmaker shows players searching (someone joined the queue).</summary>
    public bool NotifyOnQueueActivity { get; set; } = true;
    /// <summary>Notify when a new match starts on the matchmaker or for one of this PC's cabs.</summary>
    public bool NotifyOnMatch { get; set; } = true;

    // ---- Health ----
    /// <summary>Lines of card server log saved before each health event.</summary>
    public int HealthContextLines { get; set; } = 20;

    public void Normalize()
    {
        LmToClientsDelaySeconds = Math.Clamp(LmToClientsDelaySeconds, 0, 300);
        ClientStaggerSeconds = Math.Clamp(ClientStaggerSeconds, 0, 300);
        HealthContextLines = Math.Clamp(HealthContextLines, 0, 200);
        RichPresenceClientId = (RichPresenceClientId ?? "").Trim();
    }
}

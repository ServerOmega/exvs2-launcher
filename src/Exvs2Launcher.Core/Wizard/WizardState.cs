using Exvs2Launcher.Core.Identity;
using Exvs2Launcher.Core.Profiles;

namespace Exvs2Launcher.Core.Wizard;

/// <summary>
/// In-memory answers collected by the first-run wizard. Card values and the tag secret are never printed.
/// </summary>
public sealed class WizardState
{
    public string ProfileName { get; set; } = "New cab";
    public string? GameFolder { get; set; }
    public bool GenerateNewCard { get; set; }
    public CardIdentity? PendingCard { get; set; }
    public string? Serial { get; set; }
    public SerialAction SerialAction { get; set; } = SerialAction.GeneratedOnProfile;
    public string? SerialMessage { get; set; }
    public bool OnlyCabOnThisPc { get; set; } = true;
    public string? PcbIp { get; set; }
    public string? LmIp { get; set; }
    public string? CardServerPath { get; set; }
    public int CardServerPort { get; set; } = 28080;
    public string? MatchUrl { get; set; }
    public string? TagSecret { get; set; }
    public bool SyncSnowLauncherIni { get; set; } = true;
    public bool StartLm { get; set; } = true;
    public bool StartClient { get; set; } = true;
    public bool StartCardServer { get; set; } = true;
    /// <summary>When true (the default), the wizard sets every battleMode field in 29/testmode_pcb.json to 0.</summary>
    public bool SetBattleModeOnline { get; set; } = true;

    public override string ToString() =>
        "WizardState { Name = " + ProfileName +
        ", Folder = " + GameFolder +
        ", Serial = " + Serial +
        ", Card = " + (PendingCard is null ? "(none)" : PendingCard.ToString()) +
        ", GenerateNewCard = " + GenerateNewCard +
        ", PcbIp = " + PcbIp +
        ", LmIp = " + LmIp +
        ", CardServer = " + CardServerPath +
        ", MatchUrl = " + MatchUrl +
        ", TagSecret = " + Secrets.Mask(TagSecret) + " }";

    public LauncherProfile ToProfile()
    {
        return new LauncherProfile
        {
            Name = string.IsNullOrWhiteSpace(ProfileName) ? "New cab" : ProfileName.Trim(),
            GameFolder = GameFolder?.Trim() ?? "",
            CardServerPath = string.IsNullOrWhiteSpace(CardServerPath) ? null : CardServerPath.Trim(),
            CardServerPort = CardServerPort <= 0 ? 28080 : CardServerPort,
            Serial = Serial,
            PcbIp = PcbIp,
            LmIp = LmIp,
            AutoDetectIp = false,
            StartLm = StartLm,
            StartClient = StartClient,
            StartCardServer = StartCardServer,
            SyncSnowLauncherIni = SyncSnowLauncherIni,
        };
    }
}

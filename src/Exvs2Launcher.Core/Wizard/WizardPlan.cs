using Exvs2Launcher.Core.Files;
using Exvs2Launcher.Core.Identity;
using Exvs2Launcher.Core.Net;
using Exvs2Launcher.Core.Profiles;

namespace Exvs2Launcher.Core.Wizard;

public sealed record PlannedWrite(
    string Path,
    string Kind,
    string Summary,
    bool BackupFirst,
    bool Creating);

public sealed class WizardPlan
{
    public List<PlannedWrite> Writes { get; } = new();
    public List<string> Notes { get; } = new();
    public List<string> Blocking { get; } = new();
    public bool CanApply => Blocking.Count == 0;
}

/// <summary>Builds a preview of file changes and applies them (with backups) when the user confirms.</summary>
public static class WizardPlanner
{
    public static WizardPlan Preview(WizardState state, ProfileFile file, GameConfigWriter reader)
    {
        var plan = new WizardPlan();
        var folder = GameFolderValidator.Check(state.GameFolder, file, state.ProfileName);
        foreach (var issue in folder.Issues)
        {
            if (issue.Blocking) plan.Blocking.Add(issue.Message);
            else plan.Notes.Add(issue.Message);
        }
        if (!folder.CanContinue) return plan;

        var g = new GameFolder(folder.Path!);
        GameFolderState current;
        try { current = reader.Read(g.Root); }
        catch (Exception ex)
        {
            plan.Blocking.Add("Could not read the game folder: " + ex.Message);
            return plan;
        }

        if (state.GenerateNewCard)
        {
            if (state.PendingCard is null)
                plan.Blocking.Add("A new card was requested but has not been generated yet.");
            else
            {
                plan.Writes.Add(new PlannedWrite(g.CardIni, "card.ini",
                    "Write a new card " + state.PendingCard + ". Never share this.", true, !current.CardPresent));
                if (state.SyncSnowLauncherIni)
                    plan.Writes.Add(new PlannedWrite(g.SnowLauncherIni, "SnowLauncher.ini",
                        "Sync the new card into SnowLauncher.ini so SnowLauncher does not restore the old values.", true, !File.Exists(g.SnowLauncherIni)));
            }
        }
        else if (!current.CardValid)
        {
            plan.Notes.Add("No valid card.ini was kept. Generate a new card before saving, or the game will not have a card.");
        }
        else
        {
            plan.Notes.Add("Existing valid card.ini will be kept. The values stay masked and are never logged.");
        }

        if (string.IsNullOrWhiteSpace(state.Serial) || !IdentityFormat.IsValidSerial(state.Serial))
            plan.Blocking.Add("A unique 4-character serial is required.");
        else
        {
            bool writeSerial = NeedsSerialWrite(current.Serial, state.Serial, state.SerialAction);
            if (writeSerial)
            {
                plan.Writes.Add(new PlannedWrite(g.ClientIni, "config.ini",
                    SerialWriteSummary(current.Serial, state.Serial, state.SerialAction), true, false));
                if (state.SyncSnowLauncherIni)
                    plan.Writes.Add(new PlannedWrite(g.SnowLauncherIni, "SnowLauncher.ini",
                        "Sync serial " + state.Serial + " into SnowLauncher.ini.", true, !File.Exists(g.SnowLauncherIni)));
            }
            else
                plan.Notes.Add("Serial " + state.Serial + " is already in config.ini and will be kept.");
            if (!string.IsNullOrWhiteSpace(state.SerialMessage)) plan.Notes.Add(state.SerialMessage);
        }

        if (string.IsNullOrWhiteSpace(state.PcbIp))
            plan.Blocking.Add("Pick a PCB (cab) IP from this PC's addresses.");
        if (LoopbackAddress.IsLoopback(state.LmIp))
            plan.Blocking.Add(LoopbackAddress.LmRejected);
        if (string.IsNullOrWhiteSpace(state.LmIp))
            plan.Blocking.Add("Pick an LM IP. For a single cab it may match the PCB IP; for more than one cab on this PC pick a second address.");
        else if (!LoopbackAddress.IsLoopback(state.LmIp) && !string.IsNullOrWhiteSpace(state.PcbIp))
        {
            bool pcbChange = !NetworkBinding.SameIp(current.ClientIp, state.PcbIp);
            bool lmChange = !NetworkBinding.SameIp(current.LmIp, state.LmIp);
            if (pcbChange)
                plan.Writes.Add(new PlannedWrite(g.ClientIni, "config.ini",
                    "Set PCB IpAddress " + (current.ClientIp ?? "(none)") + " → " + state.PcbIp + ".", true, false));
            if (lmChange)
                plan.Writes.Add(new PlannedWrite(g.LmIni, "lmnfig.ini",
                    "Set LM IpAddress " + (current.LmIp ?? "(none)") + " → " + state.LmIp + ".", true, false));
            if (!pcbChange && !lmChange)
                plan.Notes.Add("PCB and LM IPs already match the values in the game files.");
            if (NetworkBinding.SameIp(state.PcbIp, state.LmIp) && !state.OnlyCabOnThisPc)
                plan.Notes.Add("PCB IP and LM IP are the same, but you said this PC has more than one cab. A separate LM address is recommended.");
            if (state.SyncSnowLauncherIni && (pcbChange || lmChange))
                plan.Writes.Add(new PlannedWrite(g.SnowLauncherIni, "SnowLauncher.ini",
                    "Sync PCB and LM IPs into SnowLauncher.ini.", true, !File.Exists(g.SnowLauncherIni)));
        }

        if (string.IsNullOrWhiteSpace(state.CardServerPath))
            plan.Notes.Add("No card server path yet. You can set it later, but the game will not go online without it.");
        else
        {
            var info = CardServerInspector.Inspect(state.CardServerPath);
            if (info.LooksOld && info.Warning is not null) plan.Notes.Add(info.Warning);
            var json = CardServerConfig.FindServerJson(state.CardServerPath);
            if (json is null)
                plan.Notes.Add("Could not find Configurations/server.json next to the card server. The matchmaker address and tag secret will not be written.");
            else
            {
                var pub = CardServerConfig.ReadPublic(json);
                bool writeUrl = false;
                if (!string.IsNullOrWhiteSpace(state.MatchUrl))
                {
                    if (CardServerConfig.TryNormalizeMatchUrl(state.MatchUrl, out var url, out _, out var err))
                        writeUrl = !string.Equals(pub?.MatchUrl, url, StringComparison.Ordinal);
                    else if (err is not null)
                        plan.Blocking.Add(err);
                }
                bool writeSecret = !string.IsNullOrWhiteSpace(state.TagSecret);
                if (writeUrl || writeSecret)
                {
                    var parts = new List<string>();
                    if (writeUrl) parts.Add("set MatchUrl");
                    if (writeSecret) parts.Add("set TagSecret " + Secrets.Mask(state.TagSecret));
                    plan.Writes.Add(new PlannedWrite(json, "server.json",
                        "Update the card server config: " + string.Join(" and ", parts) + ". The tag secret is stored only in this file and is never logged.",
                        true, false));
                }
                else
                    plan.Notes.Add("Card server config already has the requested matchmaker settings" +
                                   (pub?.HasTagSecret == true ? " (tag secret already set)." : "."));
            }
        }

        plan.Notes.Add("A profile named \"" + (string.IsNullOrWhiteSpace(state.ProfileName) ? "New cab" : state.ProfileName.Trim()) + "\" will be saved in the launcher profile list.");
        Dedup(plan);
        return plan;
    }

    public static WriteReport Apply(WizardState state, ProfileFile file, GameConfigWriter writer, BackupService? backups = null)
    {
        var preview = Preview(state, file, writer);
        var report = new WriteReport();
        if (!preview.CanApply)
        {
            report.Notes.AddRange(preview.Blocking);
            report.Notes.Add("No files were written because the plan has blocking issues.");
            return report;
        }

        if (state.GenerateNewCard && state.PendingCard is not null)
            report.Merge(writer.ApplyCard(state.GameFolder!, state.PendingCard, state.SyncSnowLauncherIni));

        if (!string.IsNullOrWhiteSpace(state.Serial) &&
            NeedsSerialWrite(SafeReadSerial(writer, state.GameFolder), state.Serial, state.SerialAction))
            report.Merge(writer.ApplySerial(state.GameFolder!, state.Serial, state.SyncSnowLauncherIni));

        if (!string.IsNullOrWhiteSpace(state.PcbIp) || !string.IsNullOrWhiteSpace(state.LmIp))
            report.Merge(writer.ApplyNetwork(state.GameFolder!, state.PcbIp, state.LmIp, server: null, state.SyncSnowLauncherIni));

        if (!string.IsNullOrWhiteSpace(state.CardServerPath) &&
            (!string.IsNullOrWhiteSpace(state.MatchUrl) || !string.IsNullOrWhiteSpace(state.TagSecret)))
        {
            var json = CardServerConfig.FindServerJson(state.CardServerPath);
            if (json is not null)
                report.Merge(CardServerConfig.Apply(json, state.MatchUrl, state.TagSecret, backups));
        }

        UpsertProfile(file, state);
        report.Notes.Add("Saved launcher profile \"" + state.ToProfile().Name + "\".");
        return report;
    }

    public static void UpsertProfile(ProfileFile file, WizardState state)
    {
        var incoming = state.ToProfile();
        var existing = file.Profiles.FirstOrDefault(p =>
            string.Equals(p.Name, incoming.Name, StringComparison.OrdinalIgnoreCase) ||
            (!string.IsNullOrWhiteSpace(incoming.GameFolder) &&
             ProfileStore.Normalize(p.GameFolder) == ProfileStore.Normalize(incoming.GameFolder)));
        if (existing is null)
        {
            file.Profiles.Add(incoming);
            file.LastSelected = incoming.Name;
            return;
        }
        existing.Name = incoming.Name;
        existing.GameFolder = incoming.GameFolder;
        existing.CardServerPath = incoming.CardServerPath;
        existing.CardServerPort = incoming.CardServerPort;
        existing.Serial = incoming.Serial;
        existing.PcbIp = incoming.PcbIp;
        existing.LmIp = incoming.LmIp;
        existing.AutoDetectIp = incoming.AutoDetectIp;
        existing.StartLm = incoming.StartLm;
        existing.StartClient = incoming.StartClient;
        existing.StartCardServer = incoming.StartCardServer;
        existing.SyncSnowLauncherIni = incoming.SyncSnowLauncherIni;
        file.LastSelected = existing.Name;
    }

    private static bool NeedsSerialWrite(string? current, string? planned, SerialAction action)
    {
        if (string.IsNullOrWhiteSpace(planned)) return false;
        if (action is SerialAction.WroteBecauseMissing or SerialAction.WroteBecauseConflict or SerialAction.GeneratedOnProfile)
            return !string.Equals(current?.Trim(), planned.Trim(), StringComparison.Ordinal);
        return !string.Equals(current?.Trim(), planned.Trim(), StringComparison.Ordinal);
    }

    private static string SerialWriteSummary(string? current, string planned, SerialAction action) => action switch
    {
        SerialAction.WroteBecauseConflict => "Replace serial " + (current ?? "(none)") + " with " + planned + " because another profile already uses it.",
        SerialAction.WroteBecauseMissing or SerialAction.GeneratedOnProfile => "Write serial " + planned + (current is null ? " (folder had none)." : " (was " + current + ")."),
        _ => "Write serial " + planned + " to config.ini.",
    };

    private static string? SafeReadSerial(GameConfigWriter writer, string? folder)
    {
        if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder)) return null;
        try { return writer.Read(folder).Serial; }
        catch (Exception) { return null; }
    }

    private static void Dedup(WizardPlan plan)
    {
        var seen = new HashSet<(string Path, string Summary)>();
        var writes = plan.Writes.Where(w => seen.Add((w.Path, w.Summary))).ToList();
        plan.Writes.Clear();
        plan.Writes.AddRange(writes);
    }
}

using Exvs2Launcher.Core.Files;
using Exvs2Launcher.Core.Profiles;

namespace Exvs2Launcher.Core.Wizard;

public sealed record FolderIssue(string Code, string Message, bool Blocking);

public sealed record FolderCheck(
    string? Path,
    bool Exists,
    bool HasClientExe,
    bool HasLmExe,
    bool HasBngrw,
    bool HasConfigIni,
    bool HasLmnfig,
    bool HasCardIni,
    string? UsedByProfile,
    IReadOnlyList<FolderIssue> Issues)
{
    /// <summary>Required files are present. A folder already used by another profile is a warning, not a block.</summary>
    public bool CanContinue => Exists && HasClientExe && HasLmExe && HasBngrw && HasConfigIni && HasLmnfig
        && Issues.All(i => !i.Blocking);
}

public static class GameFolderValidator
{
    public static FolderCheck Check(string? folder, ProfileFile? file = null, string? currentProfileName = null)
    {
        var issues = new List<FolderIssue>();
        if (string.IsNullOrWhiteSpace(folder))
        {
            issues.Add(new FolderIssue("missing", "Pick the game folder that contains vsac29_Release.exe.", true));
            return new FolderCheck(null, false, false, false, false, false, false, false, null, issues);
        }

        string path;
        try { path = Path.GetFullPath(folder.Trim()); }
        catch (Exception ex)
        {
            issues.Add(new FolderIssue("invalid", "That path is not usable: " + ex.Message, true));
            return new FolderCheck(folder.Trim(), false, false, false, false, false, false, false, null, issues);
        }

        if (!Directory.Exists(path))
        {
            issues.Add(new FolderIssue("missing-dir", "That folder does not exist.", true));
            return new FolderCheck(path, false, false, false, false, false, false, false, null, issues);
        }

        var g = new GameFolder(path);
        bool client = File.Exists(g.ClientExe);
        bool lm = File.Exists(g.LmExe);
        bool dll = File.Exists(g.BngrwDll);
        bool cfg = File.Exists(g.ClientIni);
        bool lmn = File.Exists(g.LmIni);
        bool card = File.Exists(g.CardIni);

        if (!client) issues.Add(new FolderIssue("client-exe", "Missing " + GameFolder.ClientExeName + ".", true));
        if (!lm) issues.Add(new FolderIssue("lm-exe", "Missing " + GameFolder.LmExeName + ".", true));
        if (!dll) issues.Add(new FolderIssue("bngrw", "Missing " + GameFolder.BngrwDllName + ".", true));
        if (!cfg) issues.Add(new FolderIssue("config", "Missing " + GameFolder.ClientIniName + ".", true));
        if (!lmn) issues.Add(new FolderIssue("lmnfig", "Missing " + GameFolder.LmIniName + ".", true));
        if (!card) issues.Add(new FolderIssue("card", "No card.ini yet. The next step can keep a card you add, or generate a new one.", false));

        string? usedBy = null;
        if (file is not null)
        {
            var existing = ProfileStore.FindByFolder(file, path);
            if (existing is not null &&
                (currentProfileName is null ||
                 !string.Equals(existing.Name, currentProfileName, StringComparison.OrdinalIgnoreCase)))
            {
                usedBy = existing.Name;
                issues.Add(new FolderIssue("in-use",
                    "Another profile already uses this folder: \"" + existing.Name + "\". You can still continue, but the two profiles will share the same game files.",
                    false));
            }
        }

        return new FolderCheck(path, true, client, lm, dll, cfg, lmn, card, usedBy, issues);
    }
}

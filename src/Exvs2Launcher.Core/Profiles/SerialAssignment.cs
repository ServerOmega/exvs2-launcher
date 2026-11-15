using Exvs2Launcher.Core.Files;
using Exvs2Launcher.Core.Identity;

namespace Exvs2Launcher.Core.Profiles;

public enum SerialAction
{
    /// <summary>A new unique serial was stored on the profile only; no game file was written.</summary>
    GeneratedOnProfile,
    /// <summary>The profile took the serial already in the folder's config.ini. The folder was not changed.</summary>
    AdoptedExisting,
    /// <summary>The folder had no serial, so a new one was written.</summary>
    WroteBecauseMissing,
    /// <summary>The folder's serial was already used by another profile, so a new one was written.</summary>
    WroteBecauseConflict,
    /// <summary>The profile and folder already agreed on a unique serial. Nothing written.</summary>
    Unchanged,
}

public sealed record SerialDecision(
    string Serial,
    SerialAction Action,
    string Message,
    WriteReport? Write = null);

/// <summary>
/// Assigns a unique cabinet serial to a <em>new</em> profile (and on first run). It never changes the serial
/// of an existing folder automatically unless that folder has no serial, or its serial is already used by
/// another saved profile. The "New serial" button is the only way to replace a unique existing serial.
/// </summary>
public static class SerialAssignment
{
    public static SerialDecision ForNewProfile(ProfileFile file, LauncherProfile profile, IdentityGenerator ids, GameConfigWriter? reader = null)
    {
        reader ??= new GameConfigWriter();
        if (!string.IsNullOrWhiteSpace(profile.GameFolder) && Directory.Exists(profile.GameFolder))
            return ForGameFolder(file, profile, ids, reader, writeIfNeeded: true);

        var taken = TakenSet(file, profile, reader);
        if (IdentityFormat.IsValidSerial(profile.Serial) && !taken.Contains(profile.Serial!))
            return new SerialDecision(profile.Serial!, SerialAction.Unchanged, "Profile serial " + profile.Serial + " is already unique.");

        var serial = ids.NewSerial(taken);
        profile.Serial = serial;
        return new SerialDecision(serial, SerialAction.GeneratedOnProfile,
            "Assigned unique serial " + serial + " to this profile. It will be written to config.ini only if you pick a game folder that has no serial, or whose serial is already used by another profile.");
    }

    /// <summary>
    /// Called when a game folder is attached to a profile. Adopts a unique existing folder serial; writes a new
    /// serial only when the folder has none, or when that serial is already used by another profile.
    /// Pass <paramref name="writeIfNeeded"/> as false to preview without touching files.
    /// </summary>
    public static SerialDecision ForGameFolder(
        ProfileFile file, LauncherProfile profile, IdentityGenerator ids, GameConfigWriter? writer = null, bool writeIfNeeded = true)
    {
        writer ??= new GameConfigWriter();
        string? existing = null;
        if (!string.IsNullOrWhiteSpace(profile.GameFolder) && Directory.Exists(profile.GameFolder))
        {
            try { existing = writer.Read(profile.GameFolder).Serial; }
            catch (Exception ex)
            {
                return new SerialDecision(profile.Serial ?? "", SerialAction.Unchanged,
                    "Could not read the game folder serial: " + ex.Message);
            }
        }

        var taken = TakenSet(file, profile, writer);

        if (IdentityFormat.IsValidSerial(existing))
        {
            bool conflict = taken.Contains(existing!);
            if (!conflict)
            {
                profile.Serial = existing;
                return new SerialDecision(existing!, SerialAction.AdoptedExisting,
                    "Using existing serial " + existing + " from config.ini. The folder was not changed.");
            }

            var serial = ids.NewSerial(Concat(taken, existing!));
            profile.Serial = serial;
            WriteReport? report = null;
            if (writeIfNeeded)
                report = writer.ApplySerial(profile.GameFolder, serial, profile.SyncSnowLauncherIni);
            return new SerialDecision(serial, SerialAction.WroteBecauseConflict,
                "config.ini serial " + existing + " is already used by another profile. Assigned new serial " + serial + " and " +
                (writeIfNeeded ? "wrote it to the game folder." : "would write it to the game folder."),
                report);
        }

        // Folder has no (valid) serial: write a unique one. Prefer a unique serial already on the profile.
        string chosen;
        if (IdentityFormat.IsValidSerial(profile.Serial) && !taken.Contains(profile.Serial!))
            chosen = profile.Serial!;
        else
            chosen = ids.NewSerial(taken);
        profile.Serial = chosen;
        WriteReport? written = null;
        if (writeIfNeeded && !string.IsNullOrWhiteSpace(profile.GameFolder))
            written = writer.ApplySerial(profile.GameFolder, chosen, profile.SyncSnowLauncherIni);
        return new SerialDecision(chosen, SerialAction.WroteBecauseMissing,
            "Game folder had no serial. Assigned " + chosen + (writeIfNeeded ? " and wrote it to config.ini." : "."),
            written);
    }

    private static HashSet<string> TakenSet(ProfileFile file, LauncherProfile profile, GameConfigWriter reader) =>
        new(SerialRegistry.TakenSerials(file, profile, reader), StringComparer.OrdinalIgnoreCase);

    private static IEnumerable<string> Concat(IReadOnlyCollection<string> taken, string extra)
    {
        foreach (var s in taken) yield return s;
        yield return extra;
    }
}

using Exvs2Launcher.Core.Ini;

namespace Exvs2Launcher.Core.Files;

/// <summary>
/// Known <c>[display] Resolution=</c> tokens observed in Infinite Boost config files and in the
/// public hook's documented list. The combo is also left editable so an already-present custom
/// value is not discarded. Client and LM resolutions are independent.
/// </summary>
public static class DisplayOptions
{
    /// <summary>144p, 240p, 480p, 720p, 1080p, 2k, 4k, 8k — 480p was observed in live cab files; the rest appear in backups and the hook's documented set.</summary>
    public static readonly string[] Resolutions =
    {
        "144p", "240p", "480p", "720p", "1080p", "2k", "4k", "8k",
    };

    /// <summary>Observed / documented <c>[config] OpeningScreenSkip=</c> tokens in the game INI (not the SnowLauncher integer).</summary>
    public static readonly string[] OpeningScreenSkipValues =
    {
        "None", "SkipReminder", "SkipBrand", "SkipAll",
    };

    public static bool IsKnownResolution(string? value) =>
        IndexOf(Resolutions, value) >= 0;

    public static bool IsKnownOpeningScreenSkip(string? value) =>
        IndexOf(OpeningScreenSkipValues, value) >= 0;

    /// <summary>8k is documented as needing windowed + borderless.</summary>
    public static bool RequiresBorderlessWindowed(string? resolution) =>
        EqualsToken(resolution, "8k");

    public static string? CanonicalResolution(string? value)
    {
        int i = IndexOf(Resolutions, value);
        if (i >= 0) return Resolutions[i];
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    public static string? CanonicalOpeningScreenSkip(string? value)
    {
        int i = IndexOf(OpeningScreenSkipValues, value);
        if (i >= 0) return OpeningScreenSkipValues[i];
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    /// <summary>
    /// SnowLauncher.ini stores OpeningScreenSkip as an integer. The only observed pairing is
    /// game <c>SkipAll</c> ↔ SnowLauncher <c>3</c>. Other integers are left alone rather than invented.
    /// </summary>
    public static string? SnowLauncherOpeningFromGame(string? gameValue) =>
        EqualsToken(CanonicalOpeningScreenSkip(gameValue), "SkipAll") ? "3" : null;

    public static IReadOnlyList<string> ResolutionChoices(params string?[] current)
    {
        var list = new List<string>(Resolutions);
        foreach (var value in current)
        {
            var canon = CanonicalResolution(value);
            if (canon is null) continue;
            if (!list.Any(x => EqualsToken(x, canon))) list.Add(canon);
        }
        return list;
    }

    public static IReadOnlyList<string> OpeningChoices(params string?[] current)
    {
        var list = new List<string>(OpeningScreenSkipValues);
        foreach (var value in current)
        {
            var canon = CanonicalOpeningScreenSkip(value);
            if (canon is null) continue;
            if (!list.Any(x => EqualsToken(x, canon))) list.Add(canon);
        }
        return list;
    }

    internal static bool EqualsToken(string? a, string? b) =>
        !string.IsNullOrWhiteSpace(a) && !string.IsNullOrWhiteSpace(b)
        && a.Trim().Equals(b.Trim(), StringComparison.OrdinalIgnoreCase);

    private static int IndexOf(string[] known, string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return -1;
        for (int i = 0; i < known.Length; i++)
            if (EqualsToken(known[i], value)) return i;
        return -1;
    }
}

/// <summary>Display / window keys for one process (client = config.ini, LM = lmnfig.ini).</summary>
public sealed class DisplayTargetSettings
{
    public string? Resolution { get; set; }
    public bool? Windowed { get; set; }
    public bool? BorderlessWindow { get; set; }
    public bool? FramerateLimit { get; set; }
    public string? OpeningScreenSkip { get; set; }

    public string Summary
    {
        get
        {
            var bits = new List<string>();
            if (!string.IsNullOrWhiteSpace(Resolution)) bits.Add(Resolution.Trim());
            if (Windowed == true) bits.Add(BorderlessWindow == true ? "borderless" : "windowed");
            else if (Windowed == false) bits.Add("fullscreen");
            if (FramerateLimit == true) bits.Add("framerate limit");
            if (!string.IsNullOrWhiteSpace(OpeningScreenSkip)) bits.Add("skip " + OpeningScreenSkip.Trim());
            return bits.Count == 0 ? "(not set)" : string.Join(", ", bits);
        }
    }
}

/// <summary>Client and LM graphics settings stored in the game folder.</summary>
public sealed class DisplayFileState
{
    public DisplayTargetSettings Client { get; set; } = new();
    public DisplayTargetSettings Lm { get; set; } = new();
}

/// <summary>
/// Reads and writes the observed graphics keys in <c>config.ini</c> and <c>lmnfig.ini</c>
/// (<c>[display] Resolution</c>, <c>FramerateLimit</c>; <c>[config] Windowed</c>,
/// <c>BorderlessWindow</c>, <c>OpeningScreenSkip</c>). Client and LM stay independent.
/// Unknown keys are left alone. Every file is backed up before it changes.
/// SnowLauncher.ini is only updated for <c>FramerateLimit</c> and the observed
/// <c>OpeningScreenSkip=SkipAll</c> → <c>3</c> pairing; resolution index fields are not written
/// because that table is incomplete.
/// </summary>
public sealed class DisplayConfigWriter
{
    private readonly BackupService _backups;

    public DisplayConfigWriter(BackupService? backups = null) => _backups = backups ?? new BackupService();

    public DisplayFileState Read(string gameFolder)
    {
        var g = new GameFolder(gameFolder);
        return new DisplayFileState
        {
            Client = ReadTarget(TryLoad(g.ClientIni)),
            Lm = ReadTarget(TryLoad(g.LmIni)),
        };
    }

    public WriteReport Apply(string gameFolder, DisplayFileState state, bool syncSnowLauncher)
    {
        if (state is null) throw new ArgumentNullException(nameof(state));
        var g = new GameFolder(gameFolder);
        if (!File.Exists(g.ClientIni))
            throw new FileNotFoundException("config.ini was not found in the game folder.", g.ClientIni);

        var report = new WriteReport();
        Edit(g.ClientIni, createIfMissing: false, doc => WriteTarget(doc, state.Client, "config.ini", report), report);
        if (File.Exists(g.LmIni))
            Edit(g.LmIni, createIfMissing: false, doc => WriteTarget(doc, state.Lm, "lmnfig.ini", report), report);
        else
            report.Notes.Add("lmnfig.ini was not found, so LM graphics were not written.");

        if (syncSnowLauncher && File.Exists(g.SnowLauncherIni))
            Edit(g.SnowLauncherIni, createIfMissing: false, doc => WriteSnow(doc, state, report), report);

        if (DisplayOptions.RequiresBorderlessWindowed(state.Client.Resolution)
            && (state.Client.Windowed != true || state.Client.BorderlessWindow != true))
            report.Notes.Add("8k on the client is documented as needing Windowed and BorderlessWindow both on.");
        if (DisplayOptions.RequiresBorderlessWindowed(state.Lm.Resolution)
            && (state.Lm.Windowed != true || state.Lm.BorderlessWindow != true))
            report.Notes.Add("8k on the LM is documented as needing Windowed and BorderlessWindow both on.");

        return report;
    }

    private static DisplayTargetSettings ReadTarget(IniDocument? doc)
    {
        var t = new DisplayTargetSettings();
        if (doc is null) return t;
        t.Resolution = DisplayOptions.CanonicalResolution(doc.Get("display", "Resolution"));
        t.FramerateLimit = ParseBool(doc.Get("display", "FramerateLimit"));
        t.Windowed = ParseBool(doc.Get("config", "Windowed"));
        t.BorderlessWindow = ParseBool(doc.Get("config", "BorderlessWindow"));
        t.OpeningScreenSkip = DisplayOptions.CanonicalOpeningScreenSkip(doc.Get("config", "OpeningScreenSkip"));
        return t;
    }

    private static bool WriteTarget(IniDocument doc, DisplayTargetSettings t, string fileLabel, WriteReport report)
    {
        bool changed = false;
        var resolution = DisplayOptions.CanonicalResolution(t.Resolution);
        var skip = DisplayOptions.CanonicalOpeningScreenSkip(t.OpeningScreenSkip);
        changed |= SetLogged(doc, "display", "Resolution", resolution, fileLabel, report, skipIfMissingAndEmpty: true);
        if (t.FramerateLimit is bool fl)
            changed |= SetLogged(doc, "display", "FramerateLimit", BoolText(fl), fileLabel, report);
        if (t.Windowed is bool windowed)
            changed |= SetLogged(doc, "config", "Windowed", BoolText(windowed), fileLabel, report);
        if (t.BorderlessWindow is bool borderless)
            changed |= SetLogged(doc, "config", "BorderlessWindow", BoolText(borderless), fileLabel, report);
        changed |= SetLogged(doc, "config", "OpeningScreenSkip", skip, fileLabel, report, skipIfMissingAndEmpty: true);
        return changed;
    }

    private static bool WriteSnow(IniDocument doc, DisplayFileState state, WriteReport report)
    {
        bool changed = false;
        if (state.Client.FramerateLimit is bool clientFl && state.Lm.FramerateLimit is bool lmFl && clientFl != lmFl)
        {
            report.Notes.Add("SnowLauncher.ini FramerateLimit was left alone because the client and LM values differ.");
        }
        else
        {
            var fl = state.Client.FramerateLimit ?? state.Lm.FramerateLimit;
            if (fl is bool value)
                changed |= SetLogged(doc, "Game_Config", "FramerateLimit", BoolText(value), "SnowLauncher.ini", report);
        }

        var clientSkip = DisplayOptions.CanonicalOpeningScreenSkip(state.Client.OpeningScreenSkip);
        var lmSkip = DisplayOptions.CanonicalOpeningScreenSkip(state.Lm.OpeningScreenSkip);
        if (clientSkip is not null && lmSkip is not null && !DisplayOptions.EqualsToken(clientSkip, lmSkip))
        {
            report.Notes.Add("SnowLauncher.ini OpeningScreenSkip was left alone because the client and LM values differ.");
        }
        else
        {
            var snow = DisplayOptions.SnowLauncherOpeningFromGame(clientSkip ?? lmSkip);
            if (snow is not null)
                changed |= SetLogged(doc, "Game_Config", "OpeningScreenSkip", snow, "SnowLauncher.ini", report);
            else if ((clientSkip ?? lmSkip) is string other)
                report.Notes.Add("SnowLauncher.ini OpeningScreenSkip was left alone; only SkipAll (3) has an observed mapping, not " + other + ".");
        }

        report.Notes.Add("SnowLauncher.ini PCB_Resolution / LM_Resolution indexes were not changed (the index table is incomplete).");
        return changed;
    }

    private static bool SetLogged(
        IniDocument doc,
        string section,
        string key,
        string? value,
        string fileLabel,
        WriteReport report,
        bool skipIfMissingAndEmpty = false)
    {
        value = value?.Trim() ?? "";
        var old = doc.Get(section, key);
        if (skipIfMissingAndEmpty && old is null && value.Length == 0) return false;
        if (old is null && value.Length == 0 && !doc.HasSection(section)) return false;
        bool changed = doc.Set(section, key, value);
        if (changed)
            report.Notes.Add($"{fileLabel} [{section}] {key}: {old ?? "(none)"} → {(value.Length == 0 ? "(empty)" : value)}");
        return changed;
    }

    private void Edit(string path, bool createIfMissing, Func<IniDocument, bool> change, WriteReport report)
    {
        bool exists = File.Exists(path);
        if (!exists && !createIfMissing) return;
        var doc = exists ? IniDocument.Load(path) : IniDocument.Empty();
        if (!change(doc))
        {
            report.Unchanged.Add(path);
            return;
        }
        if (exists)
        {
            var bak = _backups.Backup(path);
            if (bak is not null) report.Backups.Add(bak);
        }
        doc.Save(path);
        report.Written.Add(path);
    }

    private static IniDocument? TryLoad(string path) => File.Exists(path) ? IniDocument.Load(path) : null;

    private static bool? ParseBool(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        if (value.Trim().Equals("True", StringComparison.OrdinalIgnoreCase)) return true;
        if (value.Trim().Equals("False", StringComparison.OrdinalIgnoreCase)) return false;
        return null;
    }

    private static string BoolText(bool value) => value ? "True" : "False";
}

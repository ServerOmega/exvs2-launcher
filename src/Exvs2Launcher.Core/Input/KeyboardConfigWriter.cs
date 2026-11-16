using Exvs2Launcher.Core.Files;
using Exvs2Launcher.Core.Ini;

namespace Exvs2Launcher.Core.Input;

/// <summary>
/// Reads and writes the observed keyboard keys in <c>config.ini</c> and
/// <c>lmnfig.ini</c> (<c>[keyboard]</c>) and, optionally, the matching
/// <c>[Game_Client_Keyboard]</c> / <c>[Game_LM_Keyboard]</c> sections in
/// <c>SnowLauncher.ini</c>. The hook reads only <c>Enabled</c>, movement,
/// <c>A</c>/<c>B</c>/<c>C</c>/<c>D</c>, <c>Start</c>, <c>Coin</c>, <c>Card</c>,
/// <c>Test</c>, <c>Service</c>, and <c>Kill</c>. Unknown keys (for example
/// leftover <c>ButtonR1</c> lines) are left alone. Every file is backed up
/// before it changes.
/// </summary>
public sealed class KeyboardConfigWriter
{
    public const string Section = "keyboard";
    public const string SnowClientSection = "Game_Client_Keyboard";
    public const string SnowLmSection = "Game_LM_Keyboard";

    private readonly BackupService _backups;

    public KeyboardConfigWriter(BackupService? backups = null) => _backups = backups ?? new BackupService();

    public KeyboardFileState Read(string gameFolder)
    {
        var g = new GameFolder(gameFolder);
        var cfg = TryLoad(g.ClientIni);
        var lm = TryLoad(g.LmIni);
        var snow = TryLoad(g.SnowLauncherIni);
        return new KeyboardFileState
        {
            Client = ReadMap(cfg, snow, SnowClientSection),
            Lm = ReadMap(lm, snow, SnowLmSection),
        };
    }

    public WriteReport Apply(string gameFolder, KeyboardFileState state, bool syncSnowLauncher)
    {
        if (state is null) throw new ArgumentNullException(nameof(state));
        var problem = KeyboardCombos.ProblemWith(state.Client, "Client") ?? KeyboardCombos.ProblemWith(state.Lm, "LM");
        if (problem is not null) throw new ArgumentException(problem, nameof(state));

        var g = new GameFolder(gameFolder);
        if (!File.Exists(g.ClientIni))
            throw new FileNotFoundException("config.ini was not found in the game folder.", g.ClientIni);

        var report = new WriteReport();
        Edit(g.ClientIni, createIfMissing: false, doc => WriteGameIni(doc, state.Client, "config.ini", report), report);

        if (File.Exists(g.LmIni))
            Edit(g.LmIni, createIfMissing: false, doc => WriteGameIni(doc, state.Lm, "lmnfig.ini", report), report);
        else
            report.Notes.Add("lmnfig.ini was not found; LM keyboard bindings were not written.");

        if (syncSnowLauncher)
            EditIfExists(g.SnowLauncherIni, doc =>
            {
                bool changed = false;
                changed |= WriteSnow(doc, state.Client, SnowClientSection, report);
                changed |= WriteSnow(doc, state.Lm, SnowLmSection, report);
                return changed;
            }, report);

        return report;
    }

    private static KeyboardMap ReadMap(IniDocument? game, IniDocument? snow, string snowSection)
    {
        var map = new KeyboardMap();
        var src = game is not null && game.HasSection(Section) ? game : null;
        string? Get(string key) => src?.Get(Section, key) ?? snow?.Get(snowSection, key);

        var enabled = Get("Enabled");
        map.Enabled = enabled is null || IsTrue(enabled);
        map.Up = KeyboardKeyNames.NullIfEmpty(Get("Up"));
        map.Down = KeyboardKeyNames.NullIfEmpty(Get("Down"));
        map.Left = KeyboardKeyNames.NullIfEmpty(Get("Left"));
        map.Right = KeyboardKeyNames.NullIfEmpty(Get("Right"));
        map.Start = KeyboardKeyNames.NullIfEmpty(Get("Start"));
        map.Coin = KeyboardKeyNames.NullIfEmpty(Get("Coin"));
        map.Card = KeyboardKeyNames.NullIfEmpty(Get("Card"));
        map.Test = KeyboardKeyNames.NullIfEmpty(Get("Test"));
        map.Service = KeyboardKeyNames.NullIfEmpty(Get("Service"));
        map.Kill = KeyboardKeyNames.NullIfEmpty(Get("Kill"));

        // ButtonA (etc.) in the game INI [keyboard] section are not hook keys
        // (observed leftover lines such as ButtonR1). Only SnowLauncher.ini
        // stores explicit primaries and combos under those names.
        KeyboardCombos.FillFromExpanded(
            map,
            expandedA: src?.Get(Section, "A"),
            expandedB: src?.Get(Section, "B"),
            expandedC: src?.Get(Section, "C"),
            expandedD: src?.Get(Section, "D"),
            explicitA: snow?.Get(snowSection, "ButtonA"),
            explicitB: snow?.Get(snowSection, "ButtonB"),
            explicitC: snow?.Get(snowSection, "ButtonC"),
            explicitD: snow?.Get(snowSection, "ButtonD"),
            explicitAB: snow?.Get(snowSection, "ButtonAB"),
            explicitBC: snow?.Get(snowSection, "ButtonBC"),
            explicitAC: snow?.Get(snowSection, "ButtonAC"),
            explicitABC: snow?.Get(snowSection, "ButtonABC"));
        return map;
    }

    private static bool WriteGameIni(IniDocument doc, KeyboardMap map, string fileLabel, WriteReport report)
    {
        bool changed = false;
        changed |= SetLogged(doc, Section, "Enabled", map.Enabled ? "True" : "False", fileLabel, report);
        changed |= SetLogged(doc, Section, "Up", KeyboardKeyNames.NullIfEmpty(map.Up), fileLabel, report);
        changed |= SetLogged(doc, Section, "Down", KeyboardKeyNames.NullIfEmpty(map.Down), fileLabel, report);
        changed |= SetLogged(doc, Section, "Left", KeyboardKeyNames.NullIfEmpty(map.Left), fileLabel, report);
        changed |= SetLogged(doc, Section, "Right", KeyboardKeyNames.NullIfEmpty(map.Right), fileLabel, report);
        changed |= SetLogged(doc, Section, "A", KeyboardCombos.ExpandA(map), fileLabel, report);
        changed |= SetLogged(doc, Section, "B", KeyboardCombos.ExpandB(map), fileLabel, report);
        changed |= SetLogged(doc, Section, "C", KeyboardCombos.ExpandC(map), fileLabel, report);
        changed |= SetLogged(doc, Section, "D", KeyboardCombos.ExpandD(map), fileLabel, report);
        changed |= SetLogged(doc, Section, "Start", KeyboardKeyNames.NullIfEmpty(map.Start), fileLabel, report);
        changed |= SetLogged(doc, Section, "Coin", KeyboardKeyNames.NullIfEmpty(map.Coin), fileLabel, report);
        changed |= SetLogged(doc, Section, "Card", KeyboardKeyNames.NullIfEmpty(map.Card), fileLabel, report);
        changed |= SetLogged(doc, Section, "Test", KeyboardKeyNames.NullIfEmpty(map.Test), fileLabel, report);
        changed |= SetLogged(doc, Section, "Service", KeyboardKeyNames.NullIfEmpty(map.Service), fileLabel, report, skipIfMissingAndEmpty: true);
        changed |= SetLogged(doc, Section, "Kill", KeyboardKeyNames.NullIfEmpty(map.Kill), fileLabel, report);
        return changed;
    }

    private static bool WriteSnow(IniDocument doc, KeyboardMap map, string section, WriteReport report)
    {
        bool changed = false;
        const string fileLabel = "SnowLauncher.ini";
        changed |= SetLogged(doc, section, "Enabled", map.Enabled ? "True" : "False", fileLabel, report);
        changed |= SetLogged(doc, section, "Up", KeyboardKeyNames.NullIfEmpty(map.Up), fileLabel, report);
        changed |= SetLogged(doc, section, "Down", KeyboardKeyNames.NullIfEmpty(map.Down), fileLabel, report);
        changed |= SetLogged(doc, section, "Left", KeyboardKeyNames.NullIfEmpty(map.Left), fileLabel, report);
        changed |= SetLogged(doc, section, "Right", KeyboardKeyNames.NullIfEmpty(map.Right), fileLabel, report);
        changed |= SetLogged(doc, section, "ButtonA", KeyboardKeyNames.NullIfEmpty(map.ButtonA), fileLabel, report);
        changed |= SetLogged(doc, section, "ButtonB", KeyboardKeyNames.NullIfEmpty(map.ButtonB), fileLabel, report);
        changed |= SetLogged(doc, section, "ButtonC", KeyboardKeyNames.NullIfEmpty(map.ButtonC), fileLabel, report);
        changed |= SetLogged(doc, section, "ButtonD", KeyboardKeyNames.NullIfEmpty(map.ButtonD), fileLabel, report);
        changed |= SetLogged(doc, section, "ButtonAB", KeyboardKeyNames.NullIfEmpty(map.ButtonAB), fileLabel, report, skipIfMissingAndEmpty: true);
        changed |= SetLogged(doc, section, "ButtonBC", KeyboardKeyNames.NullIfEmpty(map.ButtonBC), fileLabel, report, skipIfMissingAndEmpty: true);
        changed |= SetLogged(doc, section, "ButtonAC", KeyboardKeyNames.NullIfEmpty(map.ButtonAC), fileLabel, report, skipIfMissingAndEmpty: true);
        changed |= SetLogged(doc, section, "ButtonABC", KeyboardKeyNames.NullIfEmpty(map.ButtonABC), fileLabel, report, skipIfMissingAndEmpty: true);
        changed |= SetLogged(doc, section, "Start", KeyboardKeyNames.NullIfEmpty(map.Start), fileLabel, report);
        changed |= SetLogged(doc, section, "Coin", KeyboardKeyNames.NullIfEmpty(map.Coin), fileLabel, report);
        changed |= SetLogged(doc, section, "Card", KeyboardKeyNames.NullIfEmpty(map.Card), fileLabel, report);
        changed |= SetLogged(doc, section, "Test", KeyboardKeyNames.NullIfEmpty(map.Test), fileLabel, report);
        changed |= SetLogged(doc, section, "Service", KeyboardKeyNames.NullIfEmpty(map.Service), fileLabel, report, skipIfMissingAndEmpty: true);
        changed |= SetLogged(doc, section, "Kill", KeyboardKeyNames.NullIfEmpty(map.Kill), fileLabel, report);
        return changed;
    }

    /// <summary>
    /// Sets a key when a value is provided. Empty values remove an existing key rather than
    /// writing a blank assignment, because the hook can refuse to parse an empty key name.
    /// A missing key is not created when <paramref name="skipIfMissingAndEmpty"/> is set and
    /// the new value is empty.
    /// </summary>
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
        bool changed;
        if (value.Length == 0)
        {
            changed = doc.Remove(section, key);
            if (changed)
                report.Notes.Add($"{fileLabel} [{section}] {key}: {old ?? "(none)"} → (removed)");
            return changed;
        }

        changed = doc.Set(section, key, value);
        if (changed)
            report.Notes.Add($"{fileLabel} [{section}] {key}: {old ?? "(none)"} → {value}");
        return changed;
    }

    private static bool IsTrue(string? value) =>
        value is not null && value.Trim().Equals("True", StringComparison.OrdinalIgnoreCase);

    private void EditIfExists(string path, Func<IniDocument, bool> change, WriteReport report)
    {
        if (File.Exists(path)) Edit(path, false, change, report);
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
}

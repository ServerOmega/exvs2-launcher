using Exvs2Launcher.Core.Files;
using Exvs2Launcher.Core.Ini;

namespace Exvs2Launcher.Core.Input;

/// <summary>
/// Reads and writes the observed controller keys in <c>config.ini</c>
/// (<c>[controller]</c>, <c>[controller-1]</c>, <c>[xinput]</c>) and, optionally,
/// the matching <c>[Game_Controller]</c> / <c>[Game_Controller_1]</c> sections in
/// <c>SnowLauncher.ini</c>. Unknown keys (for example arcade bitmasks) are left alone.
/// Every file is backed up before it changes.
/// </summary>
public sealed class ControllerConfigWriter
{
    public const string ClientSection = "controller";
    public const string ClientSection1 = "controller-1";
    public const string XInputSection = "xinput";
    public const string SnowSection = "Game_Controller";
    public const string SnowSection1 = "Game_Controller_1";

    private readonly BackupService _backups;

    public ControllerConfigWriter(BackupService? backups = null) => _backups = backups ?? new BackupService();

    public ControllerFileState Read(string gameFolder)
    {
        var g = new GameFolder(gameFolder);
        var cfg = TryLoad(g.ClientIni);
        var snow = TryLoad(g.SnowLauncherIni);
        var state = new ControllerFileState
        {
            Controller = ReadSlot(cfg, snow, ClientSection, SnowSection),
            Controller1 = ReadSlot(cfg, snow, ClientSection1, SnowSection1),
            XInput = ReadXInput(cfg),
        };
        return state;
    }

    public WriteReport Apply(string gameFolder, ControllerFileState state, bool syncSnowLauncher)
    {
        if (state is null) throw new ArgumentNullException(nameof(state));
        var problem = ControllerCombos.ProblemWith(state.Controller) ?? ControllerCombos.ProblemWith(state.Controller1);
        if (problem is not null) throw new ArgumentException(problem, nameof(state));
        if (state.XInput.DeviceId is int xid && (xid < ControllerCombos.MinDeviceId || xid > ControllerCombos.MaxDeviceId))
            throw new ArgumentException("xinput DeviceId must be 0-15 or 16 (all devices).");

        var g = new GameFolder(gameFolder);
        if (!File.Exists(g.ClientIni))
            throw new FileNotFoundException("config.ini was not found in the game folder.", g.ClientIni);

        var report = new WriteReport();
        Edit(g.ClientIni, createIfMissing: false, doc =>
        {
            bool changed = false;
            changed |= WriteSlotToConfig(doc, state.Controller, ClientSection, report);
            changed |= WriteSlotToConfig(doc, state.Controller1, ClientSection1, report);
            changed |= WriteXInput(doc, state.XInput, report);
            return changed;
        }, report);

        if (syncSnowLauncher)
            EditIfExists(g.SnowLauncherIni, doc =>
            {
                bool changed = false;
                changed |= WriteSlotToSnow(doc, state.Controller, SnowSection, report);
                changed |= WriteSlotToSnow(doc, state.Controller1, SnowSection1, report);
                return changed;
            }, report);

        return report;
    }

    private static ControllerSlot ReadSlot(IniDocument? cfg, IniDocument? snow, string cfgSection, string snowSection)
    {
        var slot = new ControllerSlot { Section = cfgSection };
        var src = cfg is not null && cfg.HasSection(cfgSection) ? cfg : null;
        string? Get(string key) => src?.Get(cfgSection, key) ?? snow?.Get(snowSection, key);

        slot.Enabled = IsTrue(Get("Enabled"));
        slot.Name = ControllerCombos.NullIfEmpty(Get("Name"));
        slot.Mode = ControllerCombos.NullIfEmpty(Get("Mode"));
        if (int.TryParse(Get("DeviceId"), out int deviceId)
            && deviceId is >= ControllerCombos.MinDeviceId and <= ControllerCombos.MaxDeviceId)
            slot.DeviceId = deviceId;

        ControllerCombos.FillFromExpanded(
            slot,
            expandedA: Get("A"),
            expandedB: Get("B"),
            expandedC: Get("C"),
            expandedD: Get("D"),
            explicitA: Get("ButtonA"),
            explicitB: Get("ButtonB"),
            explicitC: Get("ButtonC"),
            explicitD: Get("ButtonD"),
            explicitAB: Get("ButtonAB") ?? snow?.Get(snowSection, "ButtonAB"),
            explicitBC: Get("ButtonBC") ?? snow?.Get(snowSection, "ButtonBC"),
            explicitAC: Get("ButtonAC") ?? snow?.Get(snowSection, "ButtonAC"),
            explicitABC: Get("ButtonABC") ?? snow?.Get(snowSection, "ButtonABC"));

        // Face-button primaries and combos may come from SnowLauncher when config.ini has only expanded lists.
        if (Get("ButtonA") is null && snow?.Get(snowSection, "ButtonA") is string snowA)
            slot.ButtonA = ControllerCombos.NullIfEmpty(snowA);
        if (Get("ButtonB") is null && snow?.Get(snowSection, "ButtonB") is string snowB)
            slot.ButtonB = ControllerCombos.NullIfEmpty(snowB);
        if (Get("ButtonC") is null && snow?.Get(snowSection, "ButtonC") is string snowC)
            slot.ButtonC = ControllerCombos.NullIfEmpty(snowC);

        slot.Start = ControllerCombos.NullIfEmpty(Get("Start"));
        slot.Coin = ControllerCombos.NullIfEmpty(Get("Coin"));
        slot.Card = ControllerCombos.NullIfEmpty(Get("Card"));
        slot.Test = ControllerCombos.NullIfEmpty(Get("Test") ?? snow?.Get(snowSection, "Test"));
        slot.Service = ControllerCombos.NullIfEmpty(Get("Service") ?? snow?.Get(snowSection, "Service"));
        slot.Kill = ControllerCombos.NullIfEmpty(Get("Kill") ?? snow?.Get(snowSection, "Kill"));
        return slot;
    }

    private static XInputSettings ReadXInput(IniDocument? cfg)
    {
        var x = new XInputSettings();
        if (cfg is null || !cfg.HasSection(XInputSection)) return x;
        var enabled = cfg.Get(XInputSection, "EmulatedXInputEnabled");
        if (enabled is not null) x.EmulatedXInputEnabled = IsTrue(enabled);
        x.Mode = ControllerCombos.NullIfEmpty(cfg.Get(XInputSection, "Mode"));
        if (int.TryParse(cfg.Get(XInputSection, "DeviceId"), out int id)
            && id is >= ControllerCombos.MinDeviceId and <= ControllerCombos.MaxDeviceId)
            x.DeviceId = id;
        return x;
    }

    private static bool IsUnused(ControllerSlot slot) =>
        !slot.Enabled
        && string.IsNullOrWhiteSpace(slot.Name)
        && string.IsNullOrWhiteSpace(slot.Mode)
        && slot.DeviceId is null
        && string.IsNullOrWhiteSpace(slot.ButtonA) && string.IsNullOrWhiteSpace(slot.ButtonB)
        && string.IsNullOrWhiteSpace(slot.ButtonC) && string.IsNullOrWhiteSpace(slot.ButtonD)
        && string.IsNullOrWhiteSpace(slot.ButtonAB) && string.IsNullOrWhiteSpace(slot.ButtonBC)
        && string.IsNullOrWhiteSpace(slot.ButtonAC) && string.IsNullOrWhiteSpace(slot.ButtonABC)
        && string.IsNullOrWhiteSpace(slot.Start) && string.IsNullOrWhiteSpace(slot.Coin)
        && string.IsNullOrWhiteSpace(slot.Card) && string.IsNullOrWhiteSpace(slot.Test)
        && string.IsNullOrWhiteSpace(slot.Service) && string.IsNullOrWhiteSpace(slot.Kill);

    private static bool WriteSlotToConfig(IniDocument doc, ControllerSlot slot, string section, WriteReport report)
    {
        if (!doc.HasSection(section) && IsUnused(slot)) return false;
        bool changed = false;
        changed |= SetLogged(doc, section, "Enabled", slot.Enabled ? "True" : "False", "config.ini", report);
        changed |= SetLogged(doc, section, "Name", slot.Name ?? "", "config.ini", report);
        changed |= SetLogged(doc, section, "Mode", slot.Mode ?? "", "config.ini", report, skipIfMissingAndEmpty: true);
        changed |= SetLogged(doc, section, "DeviceId", slot.DeviceId?.ToString(), "config.ini", report, skipIfMissingAndEmpty: true);
        changed |= SetLogged(doc, section, "ButtonA", slot.ButtonA ?? "", "config.ini", report, skipIfMissingAndEmpty: true);
        changed |= SetLogged(doc, section, "ButtonB", slot.ButtonB ?? "", "config.ini", report, skipIfMissingAndEmpty: true);
        changed |= SetLogged(doc, section, "ButtonC", slot.ButtonC ?? "", "config.ini", report, skipIfMissingAndEmpty: true);
        changed |= SetLogged(doc, section, "ButtonD", slot.ButtonD ?? "", "config.ini", report, skipIfMissingAndEmpty: true);
        changed |= SetLogged(doc, section, "A", ControllerCombos.ExpandA(slot), "config.ini", report, skipIfMissingAndEmpty: true);
        changed |= SetLogged(doc, section, "B", ControllerCombos.ExpandB(slot), "config.ini", report, skipIfMissingAndEmpty: true);
        changed |= SetLogged(doc, section, "C", ControllerCombos.ExpandC(slot), "config.ini", report, skipIfMissingAndEmpty: true);
        changed |= SetLogged(doc, section, "D", ControllerCombos.ExpandD(slot), "config.ini", report, skipIfMissingAndEmpty: true);
        changed |= SetLogged(doc, section, "Start", slot.Start ?? "", "config.ini", report, skipIfMissingAndEmpty: true);
        changed |= SetLogged(doc, section, "Coin", slot.Coin ?? "", "config.ini", report, skipIfMissingAndEmpty: true);
        changed |= SetLogged(doc, section, "Card", slot.Card ?? "", "config.ini", report, skipIfMissingAndEmpty: true);
        changed |= SetLogged(doc, section, "Test", slot.Test, "config.ini", report, skipIfMissingAndEmpty: true);
        changed |= SetLogged(doc, section, "Service", slot.Service, "config.ini", report, skipIfMissingAndEmpty: true);
        changed |= SetLogged(doc, section, "Kill", slot.Kill, "config.ini", report, skipIfMissingAndEmpty: true);
        return changed;
    }

    private static bool WriteSlotToSnow(IniDocument doc, ControllerSlot slot, string section, WriteReport report)
    {
        if (!doc.HasSection(section) && IsUnused(slot)) return false;
        bool changed = false;
        changed |= SetLogged(doc, section, "Enabled", slot.Enabled ? "True" : "False", "SnowLauncher.ini", report);
        changed |= SetLogged(doc, section, "Name", slot.Name ?? "", "SnowLauncher.ini", report);
        changed |= SetLogged(doc, section, "ButtonA", slot.ButtonA ?? "", "SnowLauncher.ini", report);
        changed |= SetLogged(doc, section, "ButtonB", slot.ButtonB ?? "", "SnowLauncher.ini", report);
        changed |= SetLogged(doc, section, "ButtonC", slot.ButtonC ?? "", "SnowLauncher.ini", report);
        changed |= SetLogged(doc, section, "ButtonD", slot.ButtonD ?? "", "SnowLauncher.ini", report);
        changed |= SetLogged(doc, section, "ButtonAB", slot.ButtonAB ?? "", "SnowLauncher.ini", report);
        changed |= SetLogged(doc, section, "ButtonBC", slot.ButtonBC ?? "", "SnowLauncher.ini", report);
        changed |= SetLogged(doc, section, "ButtonAC", slot.ButtonAC ?? "", "SnowLauncher.ini", report);
        changed |= SetLogged(doc, section, "ButtonABC", slot.ButtonABC ?? "", "SnowLauncher.ini", report);
        changed |= SetLogged(doc, section, "Start", slot.Start ?? "", "SnowLauncher.ini", report);
        changed |= SetLogged(doc, section, "Coin", slot.Coin ?? "", "SnowLauncher.ini", report);
        changed |= SetLogged(doc, section, "Card", slot.Card ?? "", "SnowLauncher.ini", report);
        changed |= SetLogged(doc, section, "Test", slot.Test ?? "", "SnowLauncher.ini", report);
        changed |= SetLogged(doc, section, "Service", slot.Service ?? "", "SnowLauncher.ini", report);
        changed |= SetLogged(doc, section, "Kill", slot.Kill ?? "", "SnowLauncher.ini", report);
        return changed;
    }

    private static bool WriteXInput(IniDocument doc, XInputSettings x, WriteReport report)
    {
        bool changed = false;
        if (x.EmulatedXInputEnabled is bool enabled)
            changed |= SetLogged(doc, XInputSection, "EmulatedXInputEnabled", enabled ? "True" : "False", "config.ini", report);
        changed |= SetLogged(doc, XInputSection, "Mode", x.Mode, "config.ini", report, skipIfMissingAndEmpty: true);
        changed |= SetLogged(doc, XInputSection, "DeviceId", x.DeviceId?.ToString(), "config.ini", report, skipIfMissingAndEmpty: true);
        return changed;
    }

    /// <summary>
    /// Sets a key when a value is provided. Empty values clear an existing key. A missing key is not created
    /// when <paramref name="skipIfMissingAndEmpty"/> is set and the new value is empty — that keeps optional
    /// keys (Mode, Test, Service) from appearing in files that never had them.
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
        if (old is null && value.Length == 0)
        {
            // Do not invent empty optional keys on a section that does not exist yet unless we have real content.
            if (!doc.HasSection(section)) return false;
        }
        bool changed = doc.Set(section, key, value);
        if (changed)
            report.Notes.Add($"{fileLabel} [{section}] {key}: {old ?? "(none)"} → {(value.Length == 0 ? "(empty)" : value)}");
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

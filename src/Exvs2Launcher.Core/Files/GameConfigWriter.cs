using Exvs2Launcher.Core.Identity;
using Exvs2Launcher.Core.Ini;
using Exvs2Launcher.Core.Net;

namespace Exvs2Launcher.Core.Files;

/// <summary>What a write did. File paths only; never contains card values.</summary>
public sealed class WriteReport
{
    public List<string> Written { get; } = new();
    public List<string> Backups { get; } = new();
    public List<string> Unchanged { get; } = new();
    public List<string> Notes { get; } = new();

    public void Merge(WriteReport other)
    {
        Written.AddRange(other.Written); Backups.AddRange(other.Backups);
        Unchanged.AddRange(other.Unchanged); Notes.AddRange(other.Notes);
    }
}

/// <summary>Current identity / network values of a game folder (card values masked).</summary>
public sealed record GameFolderState(
    string? Serial,
    string? SnowLauncherSerial,
    bool CardPresent,
    bool CardValid,
    string? ClientIp,
    string? LmIp,
    string? ClientServer,
    string? LmServer,
    string? LmDisableSocketHook,
    string? SnowDisableSocketHook);

/// <summary>
/// Writes the serial, card and IP/server fields into a game folder's own files:
/// <list type="bullet">
/// <item><c>config.ini</c> [config] <c>serial</c>, <c>IpAddress</c>, <c>Server</c></item>
/// <item><c>lmnfig.ini</c> [config] <c>IpAddress</c>, <c>Server</c></item>
/// <item><c>card.ini</c> [card] <c>accessCode</c>, <c>chipId</c></item>
/// <item>optionally <c>SnowLauncher.ini</c> [Game_Config] (<c>Serial</c>, <c>ChipId</c>, <c>AccessCode</c>,
/// <c>PCB_IpAddress</c>, <c>LM_IpAddress</c>), so opening SnowLauncher later does not put the old values back.</item>
/// </list>
/// Every file is backed up before it changes; a file whose values are already right is not touched.
/// </summary>
public sealed class GameConfigWriter
{
    private readonly BackupService _backups;

    public GameConfigWriter(BackupService? backups = null) => _backups = backups ?? new BackupService();

    public GameFolderState Read(string gameFolder)
    {
        var g = new GameFolder(gameFolder);
        IniDocument? cfg = TryLoad(g.ClientIni), lm = TryLoad(g.LmIni), card = TryLoad(g.CardIni), snow = TryLoad(g.SnowLauncherIni);
        // Key names are matched case-insensitively (accessCode / AccessCode, chipId / ChipId).
        string? access = IdentityFormat.NormalizeIniValue(card?.Get("card", "accessCode") ?? "");
        string? chip = IdentityFormat.NormalizeIniValue(card?.Get("card", "chipId") ?? "");
        if (access.Length == 0) access = null;
        if (chip.Length == 0) chip = null;
        return new GameFolderState(
            cfg?.Get("config", "serial"),
            snow?.Get("Game_Config", "Serial"),
            !string.IsNullOrEmpty(access) || !string.IsNullOrEmpty(chip),
            IdentityFormat.IsValidAccessCode(access) && IdentityFormat.IsValidChipId(chip),
            cfg?.Get("config", "IpAddress"),
            lm?.Get("config", "IpAddress"),
            cfg?.Get("config", "Server"),
            lm?.Get("config", "Server"),
            lm?.Get("config", SocketHookSetting.Key),
            snow?.Get("Game_Config", SocketHookSetting.Key));
    }

    public WriteReport ApplySerial(string gameFolder, string serial, bool syncSnowLauncher)
    {
        if (!IdentityFormat.IsValidSerial(serial)) throw new ArgumentException("A serial is 4 letters or digits.", nameof(serial));
        var g = new GameFolder(gameFolder);
        if (!File.Exists(g.ClientIni)) throw new FileNotFoundException("config.ini was not found in the game folder.", g.ClientIni);

        var report = new WriteReport();
        Edit(g.ClientIni, false, d => d.Set("config", "serial", serial), report);
        if (syncSnowLauncher)
            EditIfExists(g.SnowLauncherIni, d => d.Set("Game_Config", "Serial", serial), report);
        return report;
    }

    public WriteReport ApplyCard(string gameFolder, CardIdentity card, bool syncSnowLauncher)
    {
        if (!IdentityFormat.IsValidAccessCode(card.AccessCode) || !IdentityFormat.IsValidChipId(card.ChipId))
            throw new ArgumentException("Card values have the wrong format.", nameof(card));
        var g = new GameFolder(gameFolder);
        if (!Directory.Exists(g.Root)) throw new DirectoryNotFoundException("Game folder not found: " + g.Root);

        var report = new WriteReport();
        Edit(g.CardIni, true, d => d.Set("card", "accessCode", card.AccessCode) | d.Set("card", "chipId", card.ChipId), report);
        if (syncSnowLauncher)
            EditIfExists(g.SnowLauncherIni, d => d.Set("Game_Config", "AccessCode", card.AccessCode) | d.Set("Game_Config", "ChipId", card.ChipId), report);
        return report;
    }

    /// <summary>Sets the PCB (client) and LM IP plus the card-server address the game talks to.</summary>
    public WriteReport ApplyNetwork(string gameFolder, string? pcbIp, string? lmIp, string? server, bool syncSnowLauncher)
    {
        var g = new GameFolder(gameFolder);
        var report = new WriteReport();

        if (File.Exists(g.ClientIni))
            Edit(g.ClientIni, false, d =>
                SetLogged(d, "config", "IpAddress", pcbIp, "config.ini", report) |
                SetLogged(d, "config", "Server", server, "config.ini", report), report);
        else
            report.Notes.Add("config.ini not found; client IP not written.");

        if (File.Exists(g.LmIni))
            Edit(g.LmIni, false, d =>
                SetLogged(d, "config", "IpAddress", lmIp, "lmnfig.ini", report) |
                SetLogged(d, "config", "Server", server, "lmnfig.ini", report), report);
        else
            report.Notes.Add("lmnfig.ini not found; LM IP not written.");

        if (syncSnowLauncher)
            EditIfExists(g.SnowLauncherIni, d =>
            {
                bool changed =
                    SetLogged(d, "Game_Config", "PCB_IpAddress", pcbIp, "SnowLauncher.ini", report) |
                    SetLogged(d, "Game_Config", "LM_IpAddress", lmIp, "SnowLauncher.ini", report) |
                    SetLogged(d, "Game_Config", "Server", server, "SnowLauncher.ini", report);
                NoteSnowHookNotPushed(d, report);
                return changed;
            }, report);
        return report;
    }

    /// <summary>
    /// Sets <c>lmnfig.ini</c> <c>DisableSocketHook=False</c> (and SnowLauncher.ini when syncing).
    /// Never writes True — SnowLauncher does that for the LM, and it makes the LM bind every address.
    /// </summary>
    public WriteReport ApplyLmSocketHookEnabled(string gameFolder, bool syncSnowLauncher)
    {
        var g = new GameFolder(gameFolder);
        var report = new WriteReport();
        if (File.Exists(g.LmIni))
            Edit(g.LmIni, false, d => SetLogged(d, "config", SocketHookSetting.Key, SocketHookSetting.EnabledValue, "lmnfig.ini", report), report);
        else
            report.Notes.Add("lmnfig.ini not found; DisableSocketHook not written.");
        if (syncSnowLauncher)
            EditIfExists(g.SnowLauncherIni, d =>
                SetLogged(d, "Game_Config", SocketHookSetting.Key, SocketHookSetting.EnabledValue, "SnowLauncher.ini", report), report);
        return report;
    }

    private static void NoteSnowHookNotPushed(IniDocument d, WriteReport report)
    {
        var hook = d.Get("Game_Config", SocketHookSetting.Key);
        if (SocketHookSetting.IsDisabled(hook))
            report.Notes.Add("SnowLauncher.ini has DisableSocketHook=True; it was not written to lmnfig.ini. Opening SnowLauncher would turn the LM hook off, so "
                             + SocketHookSetting.GrabEveryAddress + ". Use Write IPs or Save to set it to False.");
    }

    /// <summary>
    /// Sets a value when it is provided, and records a before/after note (IPs and Server only; never card values).
    /// A no-op set still logs "left as" for IpAddress / PCB_IpAddress / LM_IpAddress so the start log can
    /// confirm lmnfig.ini was not silently overwritten.
    /// </summary>
    private static bool SetLogged(IniDocument d, string section, string key, string? value, string fileLabel, WriteReport report)
    {
        if (string.IsNullOrWhiteSpace(value)) return false;
        value = value.Trim();
        if (key.Equals(SocketHookSetting.Key, StringComparison.OrdinalIgnoreCase) && SocketHookSetting.IsDisabled(value))
        {
            report.Notes.Add(fileLabel + " DisableSocketHook=True was not written. This launcher never turns the LM socket hook off.");
            return false;
        }
        var old = d.Get(section, key);
        bool changed = d.Set(section, key, value);
        bool ipKey = key.Equals("IpAddress", StringComparison.OrdinalIgnoreCase)
                     || key.Equals("PCB_IpAddress", StringComparison.OrdinalIgnoreCase)
                     || key.Equals("LM_IpAddress", StringComparison.OrdinalIgnoreCase);
        if (changed)
            report.Notes.Add($"{fileLabel} {key}: {old ?? "(none)"} → {value}");
        else if (ipKey)
            report.Notes.Add($"{fileLabel} {key} left as {old ?? value}");
        return changed;
    }

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

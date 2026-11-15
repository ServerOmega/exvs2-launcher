using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Exvs2Launcher.Core.Files;

/// <summary>One <c>battleMode</c> field inside the dotted-path testmode storage.</summary>
public sealed record BattleModeEntry(string Key, long Value);

/// <summary>Read-only view of battleMode fields in a <c>29/testmode_pcb.json</c> svd1 file.</summary>
public sealed class BattleModeInspection
{
    public bool FileExists { get; init; }
    public bool CanWrite { get; init; }
    public bool AlreadyZero { get; init; }
    public string? Error { get; init; }
    public IReadOnlyList<BattleModeEntry> Entries { get; init; } = Array.Empty<BattleModeEntry>();
    public IReadOnlyList<BattleModeEntry> Changing { get; init; } = Array.Empty<BattleModeEntry>();
    public string Summary { get; init; } = "";
}

/// <summary>
/// Sets every <c>battleMode</c> storage value in an svd1-wrapped <c>testmode_pcb.json</c> to 0
/// (online / WAN matchmaking). Other fields are left byte-for-byte intact. The file is backed up
/// first, then re-read to confirm the checksum and the new values. If the container version or
/// checksum cannot be rewritten safely, nothing is written.
/// </summary>
public static class TestModeBattleMode
{
    public const int TargetValue = 0;
    public const string FileName = "testmode_pcb.json";
    public const string RelativePath = "29/testmode_pcb.json";

    public static string PathFor(string gameFolder) =>
        System.IO.Path.Combine(new GameFolder(gameFolder).Root, "29", FileName);

    public static BattleModeInspection Inspect(string path)
    {
        if (!File.Exists(path))
        {
            return new BattleModeInspection
            {
                FileExists = false,
                CanWrite = false,
                Error = "No " + RelativePath + " was found in this game folder. The wizard will not create one. Set battleMode to 0 later if you add this file.",
                Summary = "No " + RelativePath + " was found. Nothing will be written.",
            };
        }

        byte[] data;
        try { data = File.ReadAllBytes(path); }
        catch (Exception ex)
        {
            return Fail("Could not read " + FileName + ": " + ex.Message, exists: true);
        }

        if (!Svd1Container.TryParse(data, out var svd1, out var parseError) || svd1 is null)
            return Fail(parseError ?? "Could not parse the svd1 container.", exists: true);

        if (svd1.Version != Svd1Container.SupportedVersion)
        {
            return Fail("This testmode file uses svd1 version " + svd1.Version +
                        ", which this launcher cannot safely rewrite. The file was left unchanged.", exists: true);
        }
        if (!svd1.ChecksumOk)
        {
            return Fail("The svd1 checksum does not match SHA-1(header + body). This launcher will not rewrite a file with an unknown or invalid checksum. The file was left unchanged.", exists: true);
        }

        if (!TryPatchBody(svd1.Body, out _, out var entries, out var changing, out var patchError))
            return Fail(patchError ?? "The testmode JSON could not be updated safely.", exists: true, entries: entries);

        bool already = entries.Count > 0 && changing.Count == 0;
        string summary;
        if (entries.Count == 0)
            summary = FileName + " has no battleMode fields.";
        else if (already)
            summary = "All " + entries.Count + " battleMode value(s) are already 0 (online matching). The file will not be changed.";
        else
            summary = "Will set battleMode to 0 (online matching) in " + changing.Count +
                      " of " + entries.Count + " setting group(s): " +
                      string.Join(", ", changing.Select(e => ShortKey(e.Key) + " was " + e.Value)) +
                      ". Every other field is left as it is. A backup is made first.";

        return new BattleModeInspection
        {
            FileExists = true,
            CanWrite = true,
            AlreadyZero = already,
            Entries = entries,
            Changing = changing,
            Summary = summary,
        };
    }

    public static WriteReport ApplyZero(string path, BackupService? backups = null)
    {
        var report = new WriteReport();
        var inspection = Inspect(path);
        if (!inspection.FileExists)
        {
            report.Notes.Add(inspection.Error ?? inspection.Summary);
            return report;
        }
        if (!inspection.CanWrite)
        {
            report.Notes.Add(inspection.Error ?? inspection.Summary);
            return report;
        }
        if (inspection.AlreadyZero)
        {
            report.Unchanged.Add(path);
            report.Notes.Add(inspection.Summary);
            return report;
        }

        backups ??= new BackupService();
        var original = File.ReadAllBytes(path);
        if (!Svd1Container.TryParse(original, out var svd1, out var parseError) || svd1 is null)
        {
            report.Notes.Add(parseError ?? "Could not parse the svd1 container. The file was left unchanged.");
            return report;
        }
        if (!TryPatchBody(svd1.Body, out var newBody, out _, out var changing, out var patchError) || newBody is null)
        {
            report.Notes.Add(patchError ?? "The testmode JSON could not be updated safely. The file was left unchanged.");
            return report;
        }

        byte[] packed;
        try { packed = Svd1Container.Pack(svd1.Version, newBody, svd1.Trailing); }
        catch (Exception ex)
        {
            report.Notes.Add(ex.Message);
            return report;
        }

        var bak = backups.Backup(path);
        if (bak is not null) report.Backups.Add(bak);
        try
        {
            File.WriteAllBytes(path, packed);
        }
        catch (Exception ex)
        {
            report.Notes.Add("Could not write " + FileName + ": " + ex.Message + ". The original file should still be in the backup, if one was made.");
            return report;
        }

        var verify = VerifyWritten(path, expectedOtherThanBattleMode: svd1.Body, newBody: newBody, trailing: svd1.Trailing);
        if (verify is not null)
        {
            try { File.WriteAllBytes(path, original); }
            catch (Exception ex)
            {
                report.Notes.Add("Wrote " + FileName + " but could not verify it (" + verify + "), and restoring the original also failed: " + ex.Message +
                                 (bak is null ? "" : " The backup is " + bak + "."));
                return report;
            }
            report.Notes.Add("Wrote " + FileName + " but the read-back check failed (" + verify + "). The original file was restored" +
                             (bak is null ? "." : " from " + bak + "."));
            return report;
        }

        report.Written.Add(path);
        report.Notes.Add("Set battleMode to 0 (online matching) in " + changing.Count +
                         " of " + inspection.Entries.Count + " setting group(s): " +
                         string.Join(", ", changing.Select(e => ShortKey(e.Key) + " " + e.Value + " → 0")) +
                         "." + (bak is null ? "" : " Backup: " + bak + "."));
        return report;
    }

    internal static bool TryPatchBody(
        ReadOnlySpan<byte> body,
        out byte[]? patched,
        out IReadOnlyList<BattleModeEntry> entries,
        out IReadOnlyList<BattleModeEntry> changing,
        out string? error)
    {
        patched = null;
        error = null;
        var found = new List<BattleModeEntry>();
        var patches = new List<(int Start, int Length, long From)>();

        try
        {
            var reader = new Utf8JsonReader(body, new JsonReaderOptions
            {
                CommentHandling = JsonCommentHandling.Disallow,
                AllowTrailingCommas = false,
            });
            bool seenFormat = false, formatOk = false, inStorage = false;
            int storageDepth = -1;

            while (reader.Read())
            {
                if (reader.TokenType == JsonTokenType.PropertyName)
                {
                    var name = reader.GetString() ?? "";
                    if (reader.CurrentDepth == 1 && name == "format_version")
                    {
                        seenFormat = true;
                        if (!reader.Read() || reader.TokenType != JsonTokenType.Number ||
                            !TryRawInteger(body, ref reader, out var ver) || ver != 1)
                        {
                            error = "The testmode JSON format_version is missing or is not 1. The file was left unchanged.";
                            entries = found; changing = Array.Empty<BattleModeEntry>();
                            return false;
                        }
                        formatOk = true;
                        continue;
                    }
                    if (reader.CurrentDepth == 1 && name == "storage")
                    {
                        if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject)
                        {
                            error = "The testmode JSON storage field is not an object. The file was left unchanged.";
                            entries = found; changing = Array.Empty<BattleModeEntry>();
                            return false;
                        }
                        inStorage = true;
                        storageDepth = reader.CurrentDepth;
                        continue;
                    }
                    if (inStorage && IsBattleModeKey(name))
                    {
                        if (!TryReadBattleModeValue(body, ref reader, name, found, patches, out var entryError))
                        {
                            error = entryError;
                            entries = found; changing = Array.Empty<BattleModeEntry>();
                            return false;
                        }
                    }
                }
                else if (inStorage && reader.TokenType == JsonTokenType.EndObject && reader.CurrentDepth < storageDepth)
                    inStorage = false;
            }

            if (!seenFormat || !formatOk)
            {
                error = "The testmode JSON has no format_version 1 field. The file was left unchanged.";
                entries = found; changing = Array.Empty<BattleModeEntry>();
                return false;
            }
            if (storageDepth < 0)
            {
                error = "The testmode JSON has no storage object. The file was left unchanged.";
                entries = found; changing = Array.Empty<BattleModeEntry>();
                return false;
            }
        }
        catch (JsonException ex)
        {
            error = "The testmode JSON could not be parsed: " + ex.Message + " The file was left unchanged.";
            entries = found; changing = Array.Empty<BattleModeEntry>();
            return false;
        }

        entries = found;
        changing = found.Where(e => e.Value != TargetValue).ToList();
        if (patches.Count == 0)
        {
            patched = body.ToArray();
            return true;
        }

        patches.Sort((a, b) => a.Start.CompareTo(b.Start));
        using var ms = new MemoryStream(body.Length);
        int cursor = 0;
        var zero = "0"u8;
        foreach (var p in patches)
        {
            if (p.Start < cursor || p.Start + p.Length > body.Length)
            {
                error = "Internal error: overlapping battleMode value positions. The file was left unchanged.";
                return false;
            }
            ms.Write(body[cursor..p.Start]);
            ms.Write(zero);
            cursor = p.Start + p.Length;
        }
        ms.Write(body[cursor..]);
        patched = ms.ToArray();
        return true;
    }

    private static bool TryReadBattleModeValue(
        ReadOnlySpan<byte> body,
        ref Utf8JsonReader reader,
        string key,
        List<BattleModeEntry> found,
        List<(int Start, int Length, long From)> patches,
        out string? error)
    {
        error = null;
        if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject)
        {
            error = "The battleMode entry " + ShortKey(key) + " is not a type/value object. The file was left unchanged.";
            return false;
        }
        bool sawValue = false;
        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.EndObject) break;
            if (reader.TokenType != JsonTokenType.PropertyName) continue;
            var prop = reader.GetString();
            if (!reader.Read())
            {
                error = "The battleMode entry " + ShortKey(key) + " is incomplete. The file was left unchanged.";
                return false;
            }
            if (prop == "value")
            {
                if (reader.TokenType != JsonTokenType.Number || !TryRawInteger(body, ref reader, out var n, out var start, out var length))
                {
                    error = "The battleMode entry " + ShortKey(key) + " is not a simple integer. The file was left unchanged.";
                    return false;
                }
                found.Add(new BattleModeEntry(key, n));
                if (n != TargetValue) patches.Add((start, length, n));
                sawValue = true;
            }
            else if (reader.TokenType is JsonTokenType.StartObject or JsonTokenType.StartArray)
                reader.Skip();
        }
        if (!sawValue)
        {
            error = "The battleMode entry " + ShortKey(key) + " has no numeric value field. The file was left unchanged.";
            return false;
        }
        return true;
    }

    private static bool TryRawInteger(ReadOnlySpan<byte> body, ref Utf8JsonReader reader, out long value) =>
        TryRawInteger(body, ref reader, out value, out _, out _);

    private static bool TryRawInteger(ReadOnlySpan<byte> body, ref Utf8JsonReader reader, out long value, out int start, out int length)
    {
        value = 0; start = 0; length = 0;
        if (reader.TokenType != JsonTokenType.Number) return false;
        start = checked((int)reader.TokenStartIndex);
        int end = checked((int)reader.BytesConsumed);
        if (start < 0 || end <= start || end > body.Length) return false;
        var raw = body[start..end];
        length = raw.Length;
        for (int i = 0; i < raw.Length; i++)
        {
            byte b = raw[i];
            if (i == 0 && b == (byte)'-') continue;
            if (b < (byte)'0' || b > (byte)'9') return false;
        }
        return long.TryParse(Encoding.ASCII.GetString(raw.ToArray()), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out value);
    }

    internal static bool IsBattleModeKey(string key)
    {
        if (string.IsNullOrEmpty(key)) return false;
        int dot = key.LastIndexOf('.');
        var last = dot < 0 ? key : key[(dot + 1)..];
        return last == "battleMode";
    }

    internal static string ShortKey(string key)
    {
        // ".TestModeParam.gameServerPcb.settingFile.[4].group.[0].battleMode" → "settingFile[4].group[0]"
        var t = key.Replace(".TestModeParam.gameServerPcb.", "", StringComparison.Ordinal)
                   .Replace(".[", "[", StringComparison.Ordinal);
        if (t.EndsWith(".battleMode", StringComparison.Ordinal)) t = t[..^".battleMode".Length];
        return string.IsNullOrEmpty(t) ? key : t;
    }

    private static string? VerifyWritten(string path, byte[] expectedOtherThanBattleMode, byte[] newBody, byte[] trailing)
    {
        byte[] data;
        try { data = File.ReadAllBytes(path); }
        catch (Exception ex) { return "could not read the file back: " + ex.Message; }
        if (!Svd1Container.TryParse(data, out var svd1, out var err) || svd1 is null)
            return err ?? "could not parse the written container";
        if (!svd1.ChecksumOk) return "written checksum did not match";
        if (svd1.Version != Svd1Container.SupportedVersion) return "written version was not 1";
        if (!svd1.Trailing.AsSpan().SequenceEqual(trailing)) return "trailing bytes were not preserved";
        if (!svd1.Body.AsSpan().SequenceEqual(newBody)) return "written body did not match the patched JSON";
        if (!TryPatchBody(svd1.Body, out _, out var entries, out var changing, out var patchErr))
            return patchErr ?? "could not re-parse written battleMode fields";
        if (entries.Count == 0) return "written file has no battleMode fields";
        if (changing.Count != 0) return "written file still has a non-zero battleMode";
        if (!OnlyBattleModeValuesChanged(expectedOtherThanBattleMode, svd1.Body, out var why))
            return why ?? "a field other than battleMode changed";
        return null;
    }

    /// <summary>True when two JSON bodies differ only in battleMode numeric values.</summary>
    internal static bool OnlyBattleModeValuesChanged(ReadOnlySpan<byte> before, ReadOnlySpan<byte> after, out string? why)
    {
        why = null;
        if (!TryReadStorage(before, out var a, out var errA) || a is null) { why = errA ?? "could not parse original storage"; return false; }
        if (!TryReadStorage(after, out var b, out var errB) || b is null) { why = errB ?? "could not parse new storage"; return false; }
        if (a.Count != b.Count) { why = "storage entry count changed"; return false; }
        foreach (var (key, av) in a)
        {
            if (!b.TryGetValue(key, out var bv)) { why = "storage key disappeared: " + key; return false; }
            if (IsBattleModeKey(key))
            {
                if (!TryStorageInteger(bv, "value", out var n) || n != TargetValue)
                { why = "battleMode " + key + " is " + bv + ", not 0"; return false; }
                continue;
            }
            if (av != bv) { why = "non-battleMode field changed: " + key; return false; }
        }
        return true;
    }

    private static bool TryReadStorage(ReadOnlySpan<byte> body, out Dictionary<string, string>? storage, out string? error)
    {
        storage = null;
        error = null;
        try
        {
            using var doc = JsonDocument.Parse(body.ToArray(), new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Disallow });
            if (!doc.RootElement.TryGetProperty("storage", out var store) || store.ValueKind != JsonValueKind.Object)
            {
                error = "no storage object";
                return false;
            }
            var map = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var p in store.EnumerateObject())
                map[p.Name] = p.Value.GetRawText();
            storage = map;
            return true;
        }
        catch (JsonException ex)
        {
            error = ex.Message;
            return false;
        }
    }

    private static bool TryStorageInteger(string rawObject, string property, out long value)
    {
        value = 0;
        try
        {
            using var doc = JsonDocument.Parse(rawObject);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return false;
            if (!doc.RootElement.TryGetProperty(property, out var el) || el.ValueKind != JsonValueKind.Number) return false;
            return el.TryGetInt64(out value);
        }
        catch (JsonException) { return false; }
    }

    private static BattleModeInspection Fail(string error, bool exists, IReadOnlyList<BattleModeEntry>? entries = null) =>
        new()
        {
            FileExists = exists,
            CanWrite = false,
            Error = error,
            Entries = entries ?? Array.Empty<BattleModeEntry>(),
            Changing = Array.Empty<BattleModeEntry>(),
            Summary = error,
        };
}

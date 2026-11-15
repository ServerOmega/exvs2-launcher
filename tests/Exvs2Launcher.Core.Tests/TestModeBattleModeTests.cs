using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Exvs2Launcher.Core.Files;
using Exvs2Launcher.Core.Profiles;
using Exvs2Launcher.Core.Wizard;

namespace Exvs2Launcher.Core.Tests;

public class TestModeBattleModeTests
{
    // Sanitized sample: real files use this dotted-path storage and 2-space JSON, not cab-specific values.
    public static readonly string SampleJson =
        "{\n" +
        "  \"format_version\": 1,\n" +
        "  \"storage\": {\n" +
        "    \".TestModeParam.gameServerPcb.useSettingFile\": {\n" +
        "      \"type\": 1,\n" +
        "      \"value\": 0\n" +
        "    },\n" +
        "    \".TestModeParam.gameServerPcb.settingFile.[0].group.[0].battleMode\": {\n" +
        "      \"type\": 1,\n" +
        "      \"value\": 6\n" +
        "    },\n" +
        "    \".TestModeParam.gameServerPcb.settingFile.[0].group.[1].battleMode\": {\n" +
        "      \"type\": 1,\n" +
        "      \"value\": 0\n" +
        "    },\n" +
        "    \".TestModeParam.gameServerPcb.settingFile.[0].group.[0].cpuMode\": {\n" +
        "      \"type\": 1,\n" +
        "      \"value\": 2\n" +
        "    },\n" +
        "    \".TestModeParam.coin.freePlay\": {\n" +
        "      \"type\": 1,\n" +
        "      \"value\": 1\n" +
        "    }\n" +
        "  }\n" +
        "}";

    public static readonly byte[] SampleTrailing = "keep trailing\n"u8.ToArray();

    public static byte[] Wrap(string json, int version = 1, byte[]? trailing = null) =>
        Svd1Container.Pack(version, Encoding.UTF8.GetBytes(json), trailing ?? SampleTrailing);

    [Fact]
    public void Roundtrip_of_sanitized_sample_is_byte_exact()
    {
        var original = Wrap(SampleJson);
        Assert.True(Svd1Container.TryParse(original, out var parsed, out var err), err);
        Assert.NotNull(parsed);
        Assert.True(parsed!.ChecksumOk);
        Assert.True(parsed.TryPack(out var packed, out var packErr), packErr);
        Assert.Equal(original, packed);
    }

    [Fact]
    public void Apply_sets_only_nonzero_battleMode_and_preserves_everything_else()
    {
        using var dir = new TempDir();
        var path = WriteSample(dir, SampleJson);
        var original = File.ReadAllBytes(path);
        Assert.True(Svd1Container.TryParse(original, out var before, out _), "parse original");

        var report = TestModeBattleMode.ApplyZero(path, new BackupService(() => new DateTime(2026, 11, 15, 16, 0, 0)));
        Assert.Contains(path, report.Written);
        Assert.Single(report.Backups);
        Assert.True(File.Exists(path + ".bak-20261115-160000"));
        Assert.Equal(original, File.ReadAllBytes(path + ".bak-20261115-160000"));
        Assert.Contains("6 → 0", string.Join(" ", report.Notes));

        var written = File.ReadAllBytes(path);
        Assert.True(Svd1Container.TryParse(written, out var after, out var err), err);
        Assert.NotNull(after);
        Assert.True(after!.ChecksumOk);
        Assert.Equal(1, after.Version);
        Assert.Equal(SampleTrailing, after.Trailing);
        Assert.True(TestModeBattleMode.OnlyBattleModeValuesChanged(before!.Body, after.Body, out var why), why);

        var inspect = TestModeBattleMode.Inspect(path);
        Assert.True(inspect.CanWrite);
        Assert.True(inspect.AlreadyZero);
        Assert.Equal(2, inspect.Entries.Count);
        Assert.All(inspect.Entries, e => Assert.Equal(0, e.Value));

        // Other raw field encodings are unchanged.
        var bodyText = Encoding.UTF8.GetString(after.Body);
        Assert.Contains("\"value\": 2", bodyText); // cpuMode
        Assert.Contains("\"value\": 1", bodyText); // freePlay
        Assert.DoesNotContain("\"value\": 6", bodyText);
        Assert.Equal(3, CountOccurrences(bodyText, "\"value\": 0"));
    }

    [Fact]
    public void Apply_is_a_no_op_when_every_battleMode_is_already_zero()
    {
        using var dir = new TempDir();
        var json = SampleJson.Replace("\"value\": 6", "\"value\": 0");
        var path = WriteSample(dir, json);
        var original = File.ReadAllBytes(path);
        var report = TestModeBattleMode.ApplyZero(path);
        Assert.Empty(report.Written);
        Assert.Empty(report.Backups);
        Assert.Contains(path, report.Unchanged);
        Assert.Equal(original, File.ReadAllBytes(path));
        Assert.Contains("already 0", string.Join(" ", report.Notes));
    }

    [Fact]
    public void Refuses_bad_magic_unknown_version_and_bad_checksum()
    {
        using var dir = new TempDir();

        var missing = TestModeBattleMode.Inspect(Path.Combine(dir.Root, "nope.json"));
        Assert.False(missing.FileExists);
        Assert.False(missing.CanWrite);
        Assert.Contains("No " + TestModeBattleMode.RelativePath, missing.Error, StringComparison.Ordinal);

        var plain = Path.Combine(dir.Root, "plain.json");
        File.WriteAllText(plain, SampleJson);
        var notSvd = TestModeBattleMode.Inspect(plain);
        Assert.False(notSvd.CanWrite);
        Assert.Contains("not an svd1", notSvd.Error, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(TestModeBattleMode.ApplyZero(plain).Written);
        Assert.Equal(SampleJson, File.ReadAllText(plain));

        var badHash = Wrap(SampleJson);
        badHash[20] ^= 0xFF;
        var hashPath = Path.Combine(dir.Root, "badhash.json");
        File.WriteAllBytes(hashPath, badHash);
        var hash = TestModeBattleMode.Inspect(hashPath);
        Assert.False(hash.CanWrite);
        Assert.Contains("checksum", hash.Error, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(TestModeBattleMode.ApplyZero(hashPath).Written);
        Assert.Equal(badHash, File.ReadAllBytes(hashPath));

        // Version 2: pack by rewriting the version field and fixing the digest so it is "valid" but unsupported.
        var v2 = MakeVersion(Wrap(SampleJson), 2);
        var v2Path = Path.Combine(dir.Root, "v2.json");
        File.WriteAllBytes(v2Path, v2);
        var ver = TestModeBattleMode.Inspect(v2Path);
        Assert.False(ver.CanWrite);
        Assert.Contains("version 2", ver.Error);
        Assert.Empty(TestModeBattleMode.ApplyZero(v2Path).Written);
        Assert.Equal(v2, File.ReadAllBytes(v2Path));
    }

    [Fact]
    public void Patches_two_digit_value_and_preserves_surrounding_bytes()
    {
        var json = SampleJson.Replace("\"value\": 6", "\"value\": 10");
        Assert.True(TestModeBattleMode.TryPatchBody(Encoding.UTF8.GetBytes(json), out var patched, out var entries, out var changing, out var err), err);
        Assert.NotNull(patched);
        Assert.Equal(2, entries.Count);
        Assert.Single(changing);
        Assert.Equal(10, changing[0].Value);
        var text = Encoding.UTF8.GetString(patched!);
        Assert.Contains("settingFile.[0].group.[0].battleMode", text);
        Assert.Contains("\"value\": 0", text);
        Assert.DoesNotContain("\"value\": 10", text);
        Assert.Contains("\"value\": 2", text);
        Assert.True(TestModeBattleMode.OnlyBattleModeValuesChanged(Encoding.UTF8.GetBytes(json), patched, out var why), why);
    }

    [Fact]
    public void Wizard_preview_and_apply_include_the_battleMode_write()
    {
        using var g = new TempGameFolder();
        File.WriteAllBytes(g.P("vsac29_Release.exe"), Array.Empty<byte>());
        File.WriteAllBytes(g.P("vsac29_Release_LM.exe"), Array.Empty<byte>());
        File.WriteAllBytes(g.P("bngrw.dll"), Array.Empty<byte>());
        Directory.CreateDirectory(Path.Combine(g.Root, "29"));
        File.WriteAllBytes(Path.Combine(g.Root, "29", "testmode_pcb.json"), Wrap(SampleJson));

        var state = new WizardState
        {
            ProfileName = "Cab 1",
            GameFolder = g.Root,
            Serial = "p8SJ",
            PcbIp = "192.168.1.69",
            LmIp = "192.168.1.69",
            OnlyCabOnThisPc = true,
            SetBattleModeOnline = true,
        };
        var preview = WizardPlanner.Preview(state, new ProfileFile(), new GameConfigWriter());
        Assert.Contains(preview.Writes, w => w.Kind == "testmode_pcb.json" && w.Summary.Contains("battleMode"));

        var report = WizardPlanner.Apply(state, new ProfileFile(), new GameConfigWriter());
        var inspect = TestModeBattleMode.Inspect(Path.Combine(g.Root, "29", "testmode_pcb.json"));
        Assert.True(inspect.AlreadyZero);
        Assert.Contains(report.Written, p => p.EndsWith("testmode_pcb.json", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Recognizes_only_the_battleMode_path_segment()
    {
        Assert.True(TestModeBattleMode.IsBattleModeKey(".TestModeParam.gameServerPcb.settingFile.[4].group.[0].battleMode"));
        Assert.False(TestModeBattleMode.IsBattleModeKey(".TestModeParam.gameServerPcb.settingFile.[0].group.[0].cpuMode"));
        Assert.False(TestModeBattleMode.IsBattleModeKey("battleModeFlag"));
    }

    private static string WriteSample(TempDir dir, string json)
    {
        var folder = Path.Combine(dir.Root, "29");
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, "testmode_pcb.json");
        File.WriteAllBytes(path, Wrap(json));
        return path;
    }

    private static int CountOccurrences(string text, string needle)
    {
        int n = 0, i = 0;
        while ((i = text.IndexOf(needle, i, StringComparison.Ordinal)) >= 0) { n++; i += needle.Length; }
        return n;
    }

    private static byte[] MakeVersion(byte[] packed, int version)
    {
        Assert.True(Svd1Container.TryParse(packed, out var file, out _), "parse for version rewrite");
        var header = new byte[16];
        Svd1Container.Magic.CopyTo(header, 0);
        BitConverter.TryWriteBytes(header.AsSpan(4), version);
        BitConverter.TryWriteBytes(header.AsSpan(8), (uint)file!.Body.Length);
        BitConverter.TryWriteBytes(header.AsSpan(12), 0);
        var buf = new byte[16 + file.Body.Length];
        header.CopyTo(buf, 0);
        file.Body.CopyTo(buf, 16);
        var digest = SHA1.HashData(buf);
        var result = new byte[packed.Length];
        packed.CopyTo(result, 0);
        header.CopyTo(result, 0);
        digest.CopyTo(result, 16);
        return result;
    }
}

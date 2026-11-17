using System.IO.Compression;
using System.Text;
using Exvs2Launcher.Core.Diagnostics;
using Exvs2Launcher.Core.Files;
using Exvs2Launcher.Core.Profiles;

namespace Exvs2Launcher.Core.Tests;

internal sealed class Tmp : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "exvs2-test-" + Guid.NewGuid().ToString("N"));
    public Tmp() => Directory.CreateDirectory(Path);
    public string File(string rel, string text)
    {
        var p = System.IO.Path.Combine(Path, rel);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(p)!);
        System.IO.File.WriteAllText(p, text);
        return p;
    }
    public void Dispose() { try { Directory.Delete(Path, true); } catch { } }
}

internal sealed class FakeSystemInfo : ISystemInfoSource
{
    public string SystemSummary() => "Windows 11 Pro 23H2\nGPU: NVIDIA GeForce RTX 3060, driver 32.0.15.6094";
    public string CrashEventsSummary() => "2026-11-15 22:01  BugCheck 1001  The computer has rebooted from a bugcheck. 0x00000116";
}

public class DiagnosticsBundleTests
{
    private const string Access = "12345678901234567890";
    private const string Chip = "0123456789ABCDEF0123456789ABCDEF";

    private static (Tmp Dir, ProfileFile File, string ProfilesPath, string LogPath) Setup()
    {
        var d = new Tmp();
        d.File("cab/config.ini", "[config]\nserial=AB12\nServer=127.0.0.1:28080\n");
        d.File("cab/lmnfig.ini", "[config]\nIpAddress=192.168.1.30\n");
        d.File("cab/card.ini", $"[card]\naccessCode={Access}\nchipId={Chip}\n");
        d.File("cab/vsac29_Release.exe", "MZ fake exe bytes");
        d.File("cab/bngrw.dll", "MZ fake dll");
        d.File("cab/data/asset.pak", "asset");
        d.File("mm/server/Server-Infinite.exe", "MZ");
        d.File("mm/server/Configurations/server.json", "{\"CardServerConfig\":{\"OnlineTag\":{\"TagSecret\":\"s3cr3t-tag\"},\"OnlineMatching\":{\"MatchUrl\":\"http://mm.example.org:28090/match\"}}}");
        d.File("mm/server/Logs/log-20261116.txt", $"2026-11-16 10:00:00.000 -08:00 [INF] Request GET /ui/card/getBy/{Access} from 8.8.4.4\n");
        var log = d.File("launcher/exvs2-launcher.log", $"2026-11-16 [10:00:00] Card {Chip} ready\n");
        var file = new ProfileFile();
        file.Profiles.Add(new LauncherProfile
        {
            Name = "V29", GameFolder = System.IO.Path.Combine(d.Path, "cab"),
            CardServerPath = System.IO.Path.Combine(d.Path, "mm/server/Server-Infinite.exe"),
        });
        var store = new ProfileStore(System.IO.Path.Combine(d.Path, "launcher", ProfileStore.FileName));
        store.Save(file);
        return (d, file, store.FilePath, log);
    }

    [Fact]
    public void Plan_lists_expected_entries_and_no_binaries()
    {
        var (d, file, profiles, log) = Setup();
        using var _ = d;
        var plan = new DiagnosticsBundleBuilder(new FakeSystemInfo()).Plan(file, profiles, log, null, "0.6.0");
        var names = plan.Select(e => e.ZipPath).ToList();
        Assert.Contains("README.txt", names);
        Assert.Contains("launcher/exvs2-launcher.log", names);
        Assert.Contains("launcher/profiles.json", names);
        Assert.Contains("system/system.txt", names);
        Assert.Contains("system/crash-events.txt", names);
        Assert.Contains("cabs/V29/config.ini", names);
        Assert.Contains("cabs/V29/card.ini", names);
        Assert.Contains("cabs/V29/listing.txt", names);
        Assert.Contains(names, n => n.StartsWith("cardserver/") && n.EndsWith("/server.json"));
        Assert.Contains(names, n => n.EndsWith("/logs/log-20261116.txt"));
        Assert.All(names, n => Assert.Contains(System.IO.Path.GetExtension(n), DiagnosticsBundleBuilder.AllowedExtensions));
        Assert.DoesNotContain(names, n => n.EndsWith(".exe") || n.EndsWith(".dll") || n.EndsWith(".pak"));
    }

    [Fact]
    public void Written_zip_has_no_secrets_and_no_exe_bytes()
    {
        var (d, file, profiles, log) = Setup();
        using var _ = d;
        var b = new DiagnosticsBundleBuilder(new FakeSystemInfo());
        var zipPath = System.IO.Path.Combine(d.Path, "out", b.DefaultFileName());
        DiagnosticsBundleBuilder.Write(b.Plan(file, profiles, log), zipPath);
        using var zip = ZipFile.OpenRead(zipPath);
        var all = new StringBuilder();
        foreach (var e in zip.Entries)
        {
            using var r = new StreamReader(e.Open());
            all.AppendLine(r.ReadToEnd());
        }
        var text = all.ToString();
        Assert.DoesNotContain(Access, text);
        Assert.DoesNotContain(Chip, text);
        Assert.DoesNotContain("s3cr3t-tag", text);
        Assert.DoesNotContain("8.8.4.4", text);
        Assert.DoesNotContain("MZ fake exe bytes", text);
        Assert.Contains("vsac29_Release.exe", text); // listed by name only
        Assert.Contains("http://mm.example.org:28090/match", text);
        Assert.Contains("NVIDIA", text);
    }

    [Theory]
    [InlineData("game/vsac29_Release.exe")]
    [InlineData("bngrw.dll")]
    [InlineData("../escape.txt")]
    [InlineData("/abs.txt")]
    [InlineData("db/ServerInfinite.db")]
    [InlineData("cert.pfx")]
    public void Write_refuses_unsafe_or_binary_entries(string name)
    {
        using var d = new Tmp();
        var entries = new[] { new BundleEntry(name, "x", () => "data") };
        Assert.Throws<InvalidOperationException>(() => DiagnosticsBundleBuilder.Write(entries, System.IO.Path.Combine(d.Path, "x.zip")));
        Assert.False(File.Exists(System.IO.Path.Combine(d.Path, "x.zip")));
    }

    [Fact]
    public void Listing_has_names_sizes_but_no_hashes()
    {
        using var d = new Tmp();
        d.File("a/one.txt", "hello");
        d.File("a/sub/two.txt", "x");
        var l = DiagnosticsBundleBuilder.Listing(System.IO.Path.Combine(d.Path, "a"));
        Assert.Contains("one.txt", l);
        Assert.Contains("<dir>  sub", l);
        Assert.DoesNotContain("two.txt", l); // top level only
        Assert.DoesNotMatch("[0-9a-f]{40}", l);
    }

    [Fact]
    public void Building_a_bundle_leaves_the_svd1_testmode_file_untouched()
    {
        var (d, file, profiles, log) = Setup();
        using var _ = d;
        var svd = System.IO.Path.Combine(d.Path, "cab", "29", "testmode_pcb.json");
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(svd)!);
        var bytes = Svd1Container.Pack(1, Encoding.UTF8.GetBytes(TestModeBattleModeTests.SampleJson), TestModeBattleModeTests.SampleTrailing);
        File.WriteAllBytes(svd, bytes);
        var before = File.GetLastWriteTimeUtc(svd);
        var b = new DiagnosticsBundleBuilder(new FakeSystemInfo());
        var plan = b.Plan(file, profiles, log);
        DiagnosticsBundleBuilder.Write(plan, System.IO.Path.Combine(d.Path, "z.zip"));
        Assert.Equal(bytes, File.ReadAllBytes(svd));
        Assert.Equal(before, File.GetLastWriteTimeUtc(svd));
        Assert.DoesNotContain(plan, e => e.ZipPath.Contains("testmode", StringComparison.OrdinalIgnoreCase));
    }
}

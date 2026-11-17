using System.IO.Compression;
using System.Security.Cryptography;
using Exvs2Launcher.Core.CardServer;
using Exvs2Launcher.Core.Launch;

namespace Exvs2Launcher.Core.Tests;

public class ReleaseTests
{
    private static string Fixture => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "github-releases-2026-11-16.json"));

    [Fact]
    public void Parses_the_real_release_list_and_picks_the_v050_package()
    {
        var list = ReleaseFeed.Parse(Fixture);
        Assert.Equal(new[] { "v0.5.0", "v0.4.1-pre", "v0.4.0-pre" }, list.Select(r => r.Tag));
        Assert.All(list, r => Assert.True(r.Prerelease));
        var c = PackageSelector.Latest(list, includePrereleases: true)!;
        Assert.Equal("v0.5.0", c.Release.Tag);
        Assert.Equal(PackageKind.BuildPackage, c.Kind);   // no prebuilt Windows server is published
        Assert.Equal("exvs2-matchmaking-v0.5.0.zip", c.Zip.Name);
        Assert.Equal("exvs2-matchmaking-v0.5.0.zip.sha256", c.ShaFile!.Name);
        Assert.Equal("46714de39c1cece94d4e0db0abbf69d083632bb77118cc007e3b98160d56ebbd", Sha256.FromDigest(c.Zip.Digest));
        Assert.Null(PackageSelector.Latest(list, includePrereleases: false));   // every release is a pre-release today
    }

    [Fact]
    public void A_prebuilt_windows_zip_is_preferred_and_a_release_without_sha_is_skipped()
    {
        var digest = "sha256:" + new string('a', 64);
        var withPrebuilt = new ReleaseInfo("v0.6.0", "x", false, false, null, "", new[]
        {
            new ReleaseAsset("exvs2-matchmaking-v0.6.0.zip", "https://example.invalid/p.zip", 1, digest),
            new ReleaseAsset("exvs2-cardserver-v0.6.0-win-x64.zip", "https://example.invalid/w.zip", 1, null),
            new ReleaseAsset("exvs2-cardserver-v0.6.0-win-x64.zip.sha256", "https://example.invalid/w.zip.sha256", 1, null),
        });
        var c = PackageSelector.Choose(withPrebuilt)!;
        Assert.Equal(PackageKind.Prebuilt, c.Kind);
        Assert.Equal("exvs2-cardserver-v0.6.0-win-x64.zip", c.Zip.Name);
        var noSha = new ReleaseInfo("v0.6.1", "x", false, false, null, "", new[]
            { new ReleaseAsset("exvs2-matchmaking-v0.6.1.zip", "https://example.invalid/p.zip", 1, null) });
        Assert.Null(PackageSelector.Choose(noSha));
        Assert.Equal("v0.6.0", PackageSelector.Latest(new[] { noSha, withPrebuilt }, false)!.Release.Tag);
        Assert.Null(PackageSelector.Choose(withPrebuilt with { Draft = true }));
    }

    [Theory]
    [InlineData("v0.5.0", "v0.5.1", true)]
    [InlineData("v0.5.0", "v0.5.0", false)]
    [InlineData("v0.4.1-pre", "v0.5.0", true)]
    [InlineData("v0.5.0-pre", "v0.5.0", true)]
    [InlineData("v0.5.0", "v0.5.0-pre", false)]
    [InlineData("v0.10.0", "v0.9.9", false)]
    [InlineData(null, "v0.4.0-pre", true)]
    [InlineData("custom-build", "v0.5.0", true)]
    [InlineData("v0.5.0", "not-a-version", false)]
    public void Version_compare(string? installed, string candidate, bool newer) =>
        Assert.Equal(newer, ReleaseVersion.IsNewer(installed, candidate));

    [Fact]
    public void Sha_file_and_digest_checks()
    {
        var hex = new string('b', 64);
        Assert.Equal(hex, Sha256.ParseShaFile(hex + "  exvs2-matchmaking-v0.5.0.zip\n"));
        Assert.Equal(hex, Sha256.ParseShaFile(new string('c', 64) + "  other.zip\n" + hex + " *mine.zip\n", "mine.zip"));
        Assert.Null(Sha256.ParseShaFile("not a hash"));
        Assert.Null(Sha256.FromDigest("md5:abc"));
        Assert.Null(Sha256.Verify(hex, hex, null));
        Assert.Null(Sha256.Verify(hex, null, hex));
        Assert.Contains("mismatch", Sha256.Verify(hex, new string('d', 64), null)!);
        Assert.Contains("disagree", Sha256.Verify(hex, hex, new string('d', 64))!);
        Assert.Contains("no SHA-256", Sha256.Verify(hex, null, null)!);
    }
}

public class CardServerLayoutTests
{
    [Fact]
    public void Detects_exe_server_folder_and_package_root_and_reads_the_kestrel_port()
    {
        using var dir = new TempDir();
        var server = Path.Combine(dir.Root, "pkg", "server");
        Directory.CreateDirectory(Path.Combine(server, "Configurations"));
        File.WriteAllText(Path.Combine(server, "Server-Infinite.exe"), "x");
        File.WriteAllText(Path.Combine(server, "Configurations", "kestrel.json"),
            """{ "Kestrel": { "Endpoints": { "BaseServer": { "Url": "http://0.0.0.0:28090" } } } }""");
        foreach (var p in new[] { Path.Combine(server, "Server-Infinite.exe"), server, Path.Combine(dir.Root, "pkg") })
        {
            var l = CardServerLayout.Detect(p)!;
            Assert.Equal(Path.GetFullPath(server), l.ServerDir);
            Assert.Equal(Path.GetFullPath(Path.Combine(dir.Root, "pkg")), l.Root);
            Assert.Equal(28090, l.ReadPort());
        }
        Assert.Null(CardServerLayout.Detect(dir.Root));
        Assert.Equal(28080, CardServerLayout.ForInstallFolder(Path.Combine(dir.Root, "new")).ReadPort());
    }
}

/// <summary>Builds fake server folders and zips; fakes the process / port / build side.</summary>
internal sealed class FakeCardServerWorld : IReleaseSource, IFileDownloader, ICardServerHost, IPortProbe, IBuildRunner, IDelay
{
    public readonly string Root;
    public readonly Dictionary<string, byte[]> Files = new();   // url -> content
    public readonly List<ReleaseInfo> Releases = new();
    public readonly List<string> Events = new();
    private int _nextPid = 1000;
    public readonly Dictionary<int, string> Alive = new();      // pid -> exe path
    /// <summary>A started exe whose folder contains this marker file never listens (a broken build).</summary>
    public const string BrokenMarker = "BROKEN";
    public string? ForeignListener;   // something else listens on the port

    public FakeCardServerWorld(string root) => Root = root;

    // ---- releases / downloads
    public Task<IReadOnlyList<ReleaseInfo>> GetReleasesAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<ReleaseInfo>>(Releases);
    public Task DownloadAsync(string url, string destination, IProgress<long>? progress, CancellationToken ct)
    {
        Events.Add("download " + Path.GetFileName(destination));
        File.WriteAllBytes(destination, Files[url]);
        return Task.CompletedTask;
    }
    public Task<string> GetStringAsync(string url, CancellationToken ct) => Task.FromResult(System.Text.Encoding.UTF8.GetString(Files[url]));

    /// <summary>Adds a release whose package zip, when "built", produces a server with <paramref name="version"/> in VERSION.txt.</summary>
    public PackageChoice AddPackageRelease(string tag, bool broken = false, bool badSha = false)
    {
        var src = Path.Combine(Root, "zipsrc-" + tag);
        var pkg = Path.Combine(src, "exvs2-matchmaking-" + tag);
        Directory.CreateDirectory(Path.Combine(pkg, "scripts"));
        Directory.CreateDirectory(Path.Combine(pkg, "files"));
        File.WriteAllText(Path.Combine(pkg, "scripts", "build-server.ps1"), "# fake");
        File.WriteAllText(Path.Combine(pkg, "files", "VERSION"), tag);
        File.WriteAllText(Path.Combine(pkg, "3-Start-Server.bat"), "rem");
        File.WriteAllText(Path.Combine(pkg, "build-version.txt"), tag + (broken ? " " + BrokenMarker : ""));
        var zip = Path.Combine(Root, $"exvs2-matchmaking-{tag}.zip");
        ZipFile.CreateFromDirectory(src, zip);
        var bytes = File.ReadAllBytes(zip);
        var hex = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        var zipUrl = $"https://example.invalid/{tag}/pkg.zip";
        var shaUrl = zipUrl + ".sha256";
        Files[zipUrl] = bytes;
        Files[shaUrl] = System.Text.Encoding.UTF8.GetBytes((badSha ? new string('0', 64) : hex) + $"  exvs2-matchmaking-{tag}.zip\n");
        var r = new ReleaseInfo(tag, tag, true, false, null, "", new[]
        {
            new ReleaseAsset($"exvs2-matchmaking-{tag}.zip", zipUrl, bytes.Length, "sha256:" + hex),
            new ReleaseAsset($"exvs2-matchmaking-{tag}.zip.sha256", shaUrl, 100, null),
        });
        Releases.Add(r);
        return PackageSelector.Choose(r)!;
    }

    // ---- build: like build-server.ps1, lays out <pkg>\server with defaults
    public Task<int> RunAsync(string packageDir, CancellationToken ct)
    {
        Events.Add("build");
        var v = File.ReadAllText(Path.Combine(packageDir, "build-version.txt"));
        var server = Path.Combine(packageDir, "server");
        Directory.CreateDirectory(Path.Combine(server, "Configurations"));
        Directory.CreateDirectory(Path.Combine(server, "wwwroot"));
        File.WriteAllText(Path.Combine(server, "Server-Infinite.exe"), "exe " + v);
        File.WriteAllText(Path.Combine(server, "Server-Infinite.dll"), "dll " + v);
        File.WriteAllText(Path.Combine(server, "appsettings.json"), "{}");
        File.WriteAllText(Path.Combine(server, "wwwroot", "index.html"), "new");
        File.WriteAllText(Path.Combine(server, "Configurations", "server.json"), "{\"default\":\"" + v + "\"}");
        File.WriteAllText(Path.Combine(server, "Configurations", "kestrel.json"),
            """{ "Kestrel": { "Endpoints": { "BaseServer": { "Url": "http://0.0.0.0:28080" } } } }""");
        if (v.Contains(BrokenMarker)) File.WriteAllText(Path.Combine(server, BrokenMarker), "");
        return Task.FromResult(0);
    }

    // ---- processes / port
    public IReadOnlyList<int> FindRunning(string serverDir) =>
        Alive.Where(kv => kv.Value.StartsWith(Path.GetFullPath(serverDir) + Path.DirectorySeparatorChar)).Select(kv => kv.Key).ToList();
    public int Start(string exePath, string workingDirectory)
    {
        var pid = _nextPid++;
        Alive[pid] = Path.GetFullPath(exePath);
        Events.Add($"start {pid} {File.ReadAllText(exePath)}");
        return pid;
    }
    public Task StopAsync(int pid, TimeSpan grace, CancellationToken ct)
    {
        if (Alive.Remove(pid)) Events.Add("stop " + pid);
        return Task.CompletedTask;
    }
    public bool IsAlive(int pid) => Alive.ContainsKey(pid);
    public Task<bool> IsListeningAsync(string host, int port, CancellationToken ct) =>
        Task.FromResult(ForeignListener is not null ||
                        Alive.Values.Any(exe => !File.Exists(Path.Combine(Path.GetDirectoryName(exe)!, BrokenMarker))));
    public Task WaitAsync(TimeSpan duration, CancellationToken ct) => Task.CompletedTask;

    public CardServerManager Manager() => new(this, this, this, this, this, owners: null, delay: this, now: () => new DateTime(2026, 11, 16, 16, 30, 0));
}

public class CardServerManagerTests
{
    private static readonly InstallOptions Idle = new() { CabsIdleConfirmed = true, HealthTimeout = TimeSpan.FromSeconds(5) };

    /// <summary>A running v0.5.0 install with user data.</summary>
    private static async Task<(FakeCardServerWorld W, CardServerLayout L)> InstalledWithData(TempDir dir)
    {
        var w = new FakeCardServerWorld(dir.Root);
        var layout = CardServerLayout.ForInstallFolder(Path.Combine(dir.Root, "exvs2-server"));
        var v050 = w.AddPackageRelease("v0.5.0");
        var r = await w.Manager().InstallAsync(layout, v050, new InstallOptions { StartAfterFreshInstall = true, HealthTimeout = TimeSpan.FromSeconds(5) }, null);
        Assert.True(r.Success, r.Message);
        // user data
        File.WriteAllText(Path.Combine(layout.ServerDir, "Configurations", "server.json"), "{\"CardServerConfig\":{\"OnlineMatching\":{\"MatchUrl\":\"http://mm.example.org:28090/match\"}}}");
        File.WriteAllText(Path.Combine(layout.ServerDir, "ServerInfinite.db"), "cards-v1");
        File.WriteAllText(Path.Combine(layout.ServerDir, "ServerInfinite.db-wal"), "wal");
        File.WriteAllText(Path.Combine(layout.ServerDir, "leaderboard_salt.txt"), "salt");
        File.WriteAllText(Path.Combine(layout.ServerDir, "mms_nodeids.json"), "{}");
        Directory.CreateDirectory(Path.Combine(layout.ServerDir, "wwwroot", "images", "ms"));
        File.WriteAllText(Path.Combine(layout.ServerDir, "wwwroot", "images", "ms", "a.png"), "png");
        return (w, layout);
    }

    [Fact]
    public async Task Fresh_install_builds_verifies_and_lays_out_the_package()
    {
        using var dir = new TempDir();
        var w = new FakeCardServerWorld(dir.Root);
        var layout = CardServerLayout.ForInstallFolder(Path.Combine(dir.Root, "exvs2-server"));
        var choice = w.AddPackageRelease("v0.5.0");
        var r = await w.Manager().InstallAsync(layout, choice, new InstallOptions(), null);
        Assert.True(r.Success, r.Message);
        Assert.True(layout.IsInstalled);
        Assert.Equal("v0.5.0", layout.InstalledVersion());
        Assert.True(File.Exists(Path.Combine(layout.Root, "3-Start-Server.bat")));
        Assert.True(layout.IsPackageRoot);
        Assert.DoesNotContain(w.Events, e => e.StartsWith("start"));   // not started unless asked
        Assert.False(Directory.Exists(Path.Combine(layout.StagingDir, "v0.5.0")));
        var st = InstallState.Load(layout.StateFile)!;
        Assert.Equal("BuildPackage", st.Kind);
        Assert.Equal(64, st.Sha256!.Length);
    }

    [Fact]
    public async Task Update_keeps_config_database_images_and_data_files_and_restarts()
    {
        using var dir = new TempDir();
        var (w, layout) = await InstalledWithData(dir);
        var v051 = w.AddPackageRelease("v0.5.1");
        var check = await w.Manager().CheckAsync(layout, includePrereleases: true);
        Assert.True(check.UpdateAvailable);
        Assert.Equal("v0.5.1", check.Latest!.Release.Tag);

        var r = await w.Manager().InstallAsync(layout, v051, Idle, null);
        Assert.True(r.Success, r.Message);
        Assert.Equal("exe v0.5.1", File.ReadAllText(layout.ExePath));
        Assert.Contains("mm.example.org", File.ReadAllText(Path.Combine(layout.ServerDir, "Configurations", "server.json")));
        Assert.Contains("v0.5.1", File.ReadAllText(Path.Combine(layout.ServerDir, "Configurations", "server.json.new-default")));
        Assert.Equal("cards-v1", File.ReadAllText(Path.Combine(layout.ServerDir, "ServerInfinite.db")));
        Assert.Equal("wal", File.ReadAllText(Path.Combine(layout.ServerDir, "ServerInfinite.db-wal")));
        Assert.Equal("salt", File.ReadAllText(Path.Combine(layout.ServerDir, "leaderboard_salt.txt")));
        Assert.True(File.Exists(Path.Combine(layout.ServerDir, "mms_nodeids.json")));
        Assert.Equal("png", File.ReadAllText(Path.Combine(layout.ServerDir, "wwwroot", "images", "ms", "a.png")));
        Assert.Equal("new", File.ReadAllText(Path.Combine(layout.ServerDir, "wwwroot", "index.html")));
        // backup = the whole previous folder, DB included
        Assert.NotNull(r.BackupDir);
        Assert.Equal("exe v0.5.0", File.ReadAllText(Path.Combine(r.BackupDir!, "Server-Infinite.exe")));
        Assert.Equal("cards-v1", File.ReadAllText(Path.Combine(r.BackupDir!, "ServerInfinite.db")));
        // the old server was stopped, the new one started and left running
        var stop = w.Events.FindIndex(e => e.StartsWith("stop"));
        var start = w.Events.FindLastIndex(e => e.StartsWith("start") && e.EndsWith("v0.5.1"));
        Assert.True(stop >= 0 && start > stop);
        Assert.Single(w.FindRunning(layout.ServerDir));
        Assert.Equal("v0.5.1", layout.InstalledVersion());
        Assert.Equal("v0.5.0", InstallState.Load(layout.StateFile)!.PreviousTag);
    }

    [Fact]
    public async Task Update_is_refused_until_the_cabs_are_confirmed_idle()
    {
        using var dir = new TempDir();
        var (w, layout) = await InstalledWithData(dir);
        var v051 = w.AddPackageRelease("v0.5.1");
        var r = await w.Manager().InstallAsync(layout, v051, new InstallOptions { CabsIdleConfirmed = false }, null);
        Assert.False(r.Success);
        Assert.Contains("no cab is in a match", r.Message);
        Assert.Equal("exe v0.5.0", File.ReadAllText(layout.ExePath));
        Assert.DoesNotContain(w.Events, e => e.StartsWith("download exvs2-matchmaking-v0.5.1"));
    }

    [Fact]
    public async Task A_new_build_that_never_listens_is_rolled_back_and_the_old_one_restarted()
    {
        using var dir = new TempDir();
        var (w, layout) = await InstalledWithData(dir);
        var broken = w.AddPackageRelease("v0.5.1", broken: true);
        var log = new List<string>();
        var r = await w.Manager().InstallAsync(layout, broken, Idle, new SyncProgress(log));
        Assert.False(r.Success);
        Assert.True(r.RolledBack);
        Assert.Contains("did not listen", r.Message);
        Assert.Equal("exe v0.5.0", File.ReadAllText(layout.ExePath));
        Assert.Equal("cards-v1", File.ReadAllText(Path.Combine(layout.ServerDir, "ServerInfinite.db")));
        Assert.Equal("png", File.ReadAllText(Path.Combine(layout.ServerDir, "wwwroot", "images", "ms", "a.png")));
        Assert.Contains("mm.example.org", File.ReadAllText(Path.Combine(layout.ServerDir, "Configurations", "server.json")));
        Assert.False(File.Exists(Path.Combine(layout.ServerDir, FakeCardServerWorld.BrokenMarker)));
        var running = w.FindRunning(layout.ServerDir);
        Assert.Single(running);
        Assert.Contains($"start {running[0]} exe v0.5.0", w.Events);
        Assert.Equal("v0.5.0", layout.InstalledVersion());
        Assert.Contains(log, l => l.Contains("failed new build was kept"));
    }

    [Fact]
    public async Task A_checksum_mismatch_changes_nothing()
    {
        using var dir = new TempDir();
        var (w, layout) = await InstalledWithData(dir);
        var bad = w.AddPackageRelease("v0.5.1", badSha: true);
        var before = w.Events.Count;
        var r = await w.Manager().InstallAsync(layout, bad, Idle, null);
        Assert.False(r.Success);
        Assert.Contains("Nothing was changed", r.Message);
        Assert.Contains("disagree", r.Message);
        Assert.Equal("exe v0.5.0", File.ReadAllText(layout.ExePath));
        Assert.DoesNotContain(w.Events.Skip(before), e => e.StartsWith("stop") || e == "build");
    }

    [Fact]
    public async Task A_port_taken_by_another_program_stops_the_update_before_the_swap()
    {
        using var dir = new TempDir();
        var (w, layout) = await InstalledWithData(dir);
        var v051 = w.AddPackageRelease("v0.5.1");
        w.ForeignListener = "other";
        var r = await w.Manager().InstallAsync(layout, v051, Idle, null);
        Assert.False(r.Success);
        Assert.Contains("already in use", r.Message);
        Assert.Equal("exe v0.5.0", File.ReadAllText(layout.ExePath));
    }

    [Fact]
    public async Task Start_and_stop_report_state()
    {
        using var dir = new TempDir();
        var (w, layout) = await InstalledWithData(dir);
        var m = w.Manager();
        Assert.True((await m.GetStateAsync(layout)).Running);
        await m.StopAsync(layout, null, TimeSpan.FromSeconds(1));
        var st = await m.GetStateAsync(layout);
        Assert.False(st.Running);
        Assert.StartsWith("Stopped", st.Summary);
        Assert.NotNull(await m.StartAsync(layout, null, TimeSpan.FromSeconds(5)));
        Assert.Contains("listening on 28080", (await m.GetStateAsync(layout)).Summary);
    }

    private sealed class SyncProgress : IProgress<string>
    {
        private readonly List<string> _l;
        public SyncProgress(List<string> l) => _l = l;
        public void Report(string value) => _l.Add(value);
    }
}

using System.Security.Cryptography;
using System.Text;
using Exvs2Launcher.Core.CardServer;
using Exvs2Launcher.Core.Update;

namespace Exvs2Launcher.Core.Tests;

internal sealed class FakeDownloader : IFileDownloader
{
    public Dictionary<string, byte[]> Files { get; } = new();
    public Task DownloadAsync(string url, string destination, IProgress<long>? progress, CancellationToken ct)
    {
        File.WriteAllBytes(destination, Files[url]);
        return Task.CompletedTask;
    }
    public Task<string> GetStringAsync(string url, CancellationToken ct) => Task.FromResult(Encoding.UTF8.GetString(Files[url]));
}

public class LauncherUpdateTests
{
    private static readonly byte[] Exe = Encoding.UTF8.GetBytes("pretend new launcher exe");
    private static string Hash(byte[] b) => Convert.ToHexString(SHA256.HashData(b)).ToLowerInvariant();

    private static ReleaseInfo Release(string tag, bool pre, bool withSha = true, string? body = null, string? digest = null, string? exeName = null)
    {
        exeName ??= $"Exvs2Launcher-{tag}.exe";
        var assets = new List<ReleaseAsset> { new(exeName, "https://example/" + exeName, Exe.Length, digest) };
        if (withSha) assets.Add(new(exeName + ".sha256", "https://example/" + exeName + ".sha256", 90, null));
        return new ReleaseInfo(tag, tag, pre, false, DateTimeOffset.Now, "https://example/r/" + tag, assets, body);
    }

    [Fact]
    public void Picks_newest_respecting_prerelease_toggle_and_skips_side_builds()
    {
        var rels = new[]
        {
            Release("v0.6.1", pre: true), Release("v0.6.0", pre: false), Release("v0.4.5-n2n", pre: true, exeName: "Exvs2Launcher-v0.4.5-n2n.exe"),
        };
        Assert.Equal("v0.6.1", LauncherUpdate.Latest(rels, includePrereleases: true)!.Version);
        Assert.Equal("v0.6.0", LauncherUpdate.Latest(rels, includePrereleases: false)!.Version);
        Assert.Null(LauncherUpdate.Choose(rels[2]));
        var r = LauncherUpdate.Evaluate("0.6.0", rels, false);
        Assert.False(r.UpdateAvailable);
        Assert.True(LauncherUpdate.Evaluate("0.5.1", rels, false).UpdateAvailable);
    }

    [Fact]
    public void Release_without_any_hash_is_not_offered()
    {
        Assert.Null(LauncherUpdate.Choose(Release("v0.7.0", false, withSha: false)));
        Assert.NotNull(LauncherUpdate.Choose(Release("v0.7.0", false, withSha: false, digest: "sha256:" + Hash(Exe))));
        Assert.NotNull(LauncherUpdate.Choose(Release("v0.7.0", false, withSha: false, body: Hash(Exe) + "  Exvs2Launcher-v0.7.0.exe")));
    }

    [Fact]
    public void Body_hash_prefers_line_naming_the_file()
    {
        var other = new string('a', 64);
        var body = $"{other}  something-else.zip\n{Hash(Exe)}  Exvs2Launcher-v0.7.0.exe\n";
        Assert.Equal(Hash(Exe), LauncherUpdate.ShaFromBody(body, "Exvs2Launcher-v0.7.0.exe"));
        Assert.Null(LauncherUpdate.ShaFromBody($"{other}\n{new string('b', 64)}", "x.exe"));
    }

    [Fact]
    public void Verify_requires_agreement_and_match()
    {
        var h = Hash(Exe);
        Assert.Null(LauncherUpdate.Verify(h, h, null, null));
        Assert.Null(LauncherUpdate.Verify(h, null, h, h));
        Assert.NotNull(LauncherUpdate.Verify(h, null, null, null));
        Assert.NotNull(LauncherUpdate.Verify(h, h, new string('0', 64), null));
        Assert.NotNull(LauncherUpdate.Verify(new string('1', 64), h, null, null));
    }

    [Fact]
    public async Task Download_verifies_and_deletes_bad_file()
    {
        using var d = new Tmp();
        var choice = LauncherUpdate.Choose(Release("v0.7.0", false))!;
        var dl = new FakeDownloader();
        dl.Files[choice.Exe.DownloadUrl] = Exe;
        dl.Files[choice.ShaFile!.DownloadUrl] = Encoding.UTF8.GetBytes(Hash(Exe) + "  " + choice.Exe.Name + "\n");
        var staged = await LauncherUpdate.DownloadAndVerifyAsync(choice, dl, d.Path, null, CancellationToken.None);
        Assert.Equal(Exe, File.ReadAllBytes(staged));

        dl.Files[choice.Exe.DownloadUrl] = Encoding.UTF8.GetBytes("tampered");
        await Assert.ThrowsAsync<InvalidOperationException>(() => LauncherUpdate.DownloadAndVerifyAsync(choice, dl, Path.Combine(d.Path, "2"), null, CancellationToken.None));
        Assert.False(File.Exists(Path.Combine(d.Path, "2", choice.Exe.Name)));
    }

    [Fact]
    public void Install_keeps_previous_and_rollback_restores_it()
    {
        using var d = new Tmp();
        var current = d.File("Exvs2Launcher.exe", "old");
        var staged = d.File("stage/Exvs2Launcher-v0.7.0.exe", "new");
        LauncherUpdate.Install(current, staged);
        Assert.Equal("new", File.ReadAllText(current));
        Assert.Equal("old", File.ReadAllText(current + LauncherUpdate.PreviousSuffix));
        Assert.True(LauncherUpdate.CanRollBack(current));
        LauncherUpdate.RollBack(current);
        Assert.Equal("old", File.ReadAllText(current));
        Assert.Equal("new", File.ReadAllText(current + LauncherUpdate.FailedSuffix));
        Assert.False(LauncherUpdate.CanRollBack(current));
        LauncherUpdate.CleanUp(current);
        Assert.False(File.Exists(current + LauncherUpdate.FailedSuffix));
    }

    [Fact]
    public void Install_with_missing_staged_file_changes_nothing()
    {
        using var d = new Tmp();
        var current = d.File("Exvs2Launcher.exe", "old");
        Assert.Throws<FileNotFoundException>(() => LauncherUpdate.Install(current, Path.Combine(d.Path, "nope.exe")));
        Assert.Equal("old", File.ReadAllText(current));
        Assert.False(File.Exists(current + LauncherUpdate.PreviousSuffix));
    }

    [Fact]
    public void Release_feed_keeps_body()
    {
        var r = ReleaseFeed.Parse("""[{"tag_name":"v0.6.0","name":"v0.6.0","prerelease":true,"body":"notes here","assets":[]}]""");
        Assert.Equal("notes here", r[0].Body);
    }
}

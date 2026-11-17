using System.Text.RegularExpressions;
using Exvs2Launcher.Core.CardServer;

namespace Exvs2Launcher.Core.Update;

/// <summary>A launcher release that can be installed: the exe asset and where its SHA-256 comes from.</summary>
public sealed record LauncherUpdateChoice(ReleaseInfo Release, ReleaseAsset Exe, ReleaseAsset? ShaFile, string? DigestSha, string? BodySha)
{
    public string Version => Release.Tag;
}

public sealed record UpdateCheckResult(string Installed, LauncherUpdateChoice? Latest, bool UpdateAvailable, string? Error)
{
    public string Summary => Error is not null ? "Update check failed: " + Error
        : Latest is null ? "No launcher release with a verifiable exe was found."
        : UpdateAvailable ? $"Launcher {Latest.Release.Label} is available (you have {Installed})."
        : $"The launcher is up to date ({Installed}).";
}

/// <summary>
/// Launcher self-update from GitHub releases of ServerOmega/exvs2-launcher. The exe is only installed when its
/// SHA-256 matches the release (<c>.sha256</c> asset, GitHub's asset digest, or a hash next to the file name in the
/// release notes; when more than one is present they must agree). Installing swaps files by rename and keeps the
/// previous exe as <c>Exvs2Launcher.exe.previous</c> for rollback. Nothing installs without the user clicking.
/// </summary>
public static partial class LauncherUpdate
{
    public const string Owner = "ServerOmega";
    public const string Repo = "exvs2-launcher";
    public static readonly string ApiUrl = $"https://api.github.com/repos/{Owner}/{Repo}/releases?per_page=30";
    public static readonly string ReleasesPage = $"https://github.com/{Owner}/{Repo}/releases";
    public const string PreviousSuffix = ".previous";
    public const string FailedSuffix = ".failed";

    [GeneratedRegex(@"^Exvs2Launcher[-_.A-Za-z0-9]*\.exe$", RegexOptions.IgnoreCase)]
    private static partial Regex ExeName();

    [GeneratedRegex(@"(?<![0-9A-Fa-f])[0-9A-Fa-f]{64}(?![0-9A-Fa-f])")]
    private static partial Regex Hex64();

    /// <summary>Picks the launcher exe from a release (side builds such as n2n / test exes are skipped).</summary>
    public static LauncherUpdateChoice? Choose(ReleaseInfo r)
    {
        if (r.Draft) return null;
        var exes = r.Assets.Where(a => ExeName().IsMatch(a.Name) &&
                                       !a.Name.Contains("n2n", StringComparison.OrdinalIgnoreCase) &&
                                       !a.Name.Contains("test", StringComparison.OrdinalIgnoreCase)).ToList();
        var exe = exes.FirstOrDefault(a => a.Name.Contains(r.Tag, StringComparison.OrdinalIgnoreCase)) ?? exes.FirstOrDefault();
        if (exe is null) return null;
        var sha = r.Assets.FirstOrDefault(a => a.Name.Equals(exe.Name + ".sha256", StringComparison.OrdinalIgnoreCase));
        var digest = Sha256.FromDigest(exe.Digest);
        var body = ShaFromBody(r.Body, exe.Name);
        if (sha is null && digest is null && body is null) return null;
        return new LauncherUpdateChoice(r, exe, sha, digest, body);
    }

    public static LauncherUpdateChoice? Latest(IEnumerable<ReleaseInfo> releases, bool includePrereleases) =>
        releases.Where(r => !r.Draft && (includePrereleases || !r.Prerelease))
            .Select(r => (r, v: ReleaseVersion.TryParse(r.Tag)))
            .Where(x => x.v is not null)
            .OrderByDescending(x => x.v)
            .Select(x => Choose(x.r))
            .FirstOrDefault(c => c is not null);

    public static UpdateCheckResult Evaluate(string installed, IEnumerable<ReleaseInfo> releases, bool includePrereleases)
    {
        var latest = Latest(releases, includePrereleases);
        bool newer = latest is not null && ReleaseVersion.IsNewer(installed, latest.Release.Tag);
        return new UpdateCheckResult(installed, latest, newer, null);
    }

    /// <summary>A 64-hex SHA-256 on a release-notes line that names the file; or the only one in the notes.</summary>
    public static string? ShaFromBody(string? body, string fileName)
    {
        if (string.IsNullOrWhiteSpace(body)) return null;
        var all = new List<string>();
        foreach (var line in body.Split('\n'))
        {
            var m = Hex64().Matches(line);
            foreach (Match x in m)
            {
                all.Add(x.Value.ToLowerInvariant());
                if (line.Contains(fileName, StringComparison.OrdinalIgnoreCase)) return x.Value.ToLowerInvariant();
            }
        }
        return all.Distinct().Count() == 1 ? all[0] : null;
    }

    /// <summary>
    /// Checks a downloaded exe against every known expected hash. All known values must agree and match.
    /// Returns null when it verifies, otherwise why not.
    /// </summary>
    public static string? Verify(string actual, string? fromShaFile, string? fromDigest, string? fromBody)
    {
        var known = new[] { fromShaFile, fromDigest, fromBody }.Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(x => x!.Trim().ToLowerInvariant()).ToList();
        if (known.Count == 0) return "The release has no SHA-256 for this exe, so it was not installed.";
        if (known.Distinct().Count() > 1) return "The release's checksums disagree with each other. Nothing was installed.";
        return string.Equals(actual, known[0], StringComparison.OrdinalIgnoreCase)
            ? null
            : $"Checksum mismatch: expected {known[0][..12]}…, got {(actual.Length >= 12 ? actual[..12] : actual)}…. Nothing was installed.";
    }

    /// <summary>Downloads, verifies and stages the new exe next to the current one. Returns the staged path.</summary>
    public static async Task<string> DownloadAndVerifyAsync(LauncherUpdateChoice choice, IFileDownloader downloader, string stagingDir,
        IProgress<long>? progress, CancellationToken ct)
    {
        Directory.CreateDirectory(stagingDir);
        var target = Path.Combine(stagingDir, choice.Exe.Name);
        string? fileSha = null;
        if (choice.ShaFile is not null)
        {
            var text = await downloader.GetStringAsync(choice.ShaFile.DownloadUrl, ct).ConfigureAwait(false);
            fileSha = Sha256.ParseShaFile(text, choice.Exe.Name)
                      ?? throw new InvalidOperationException("The release's .sha256 file could not be read.");
        }
        await downloader.DownloadAsync(choice.Exe.DownloadUrl, target, progress, ct).ConfigureAwait(false);
        var actual = await Sha256.ComputeAsync(target, ct).ConfigureAwait(false);
        var problem = Verify(actual, fileSha, choice.DigestSha, choice.BodySha);
        if (problem is not null)
        {
            try { File.Delete(target); } catch (Exception) { }
            throw new InvalidOperationException(problem);
        }
        return target;
    }

    /// <summary>
    /// Puts the staged exe in place of the running one: current → <c>.previous</c> (Windows allows renaming a
    /// running exe), staged → current. On failure the original is put back. The caller restarts the launcher.
    /// </summary>
    public static void Install(string currentExe, string stagedExe)
    {
        if (!File.Exists(stagedExe)) throw new FileNotFoundException("The downloaded update is missing.", stagedExe);
        var previous = currentExe + PreviousSuffix;
        if (File.Exists(previous)) File.Delete(previous);
        File.Move(currentExe, previous);
        try { File.Move(stagedExe, currentExe); }
        catch
        {
            try { File.Move(previous, currentExe); } catch (Exception) { }
            throw;
        }
    }

    public static bool CanRollBack(string currentExe) => File.Exists(currentExe + PreviousSuffix);

    /// <summary>Puts <c>.previous</c> back as the launcher exe and keeps the replaced one as <c>.failed</c>.</summary>
    public static void RollBack(string currentExe)
    {
        var previous = currentExe + PreviousSuffix;
        if (!File.Exists(previous)) throw new InvalidOperationException("There is no previous launcher exe to go back to.");
        var failed = currentExe + FailedSuffix;
        if (File.Exists(failed)) File.Delete(failed);
        File.Move(currentExe, failed);
        try { File.Move(previous, currentExe); }
        catch
        {
            try { File.Move(failed, currentExe); } catch (Exception) { }
            throw;
        }
    }

    /// <summary>Removes a leftover <c>.failed</c> exe from an earlier rollback (best effort; never the .previous).</summary>
    public static void CleanUp(string currentExe)
    {
        try { var f = currentExe + FailedSuffix; if (File.Exists(f)) File.Delete(f); } catch (Exception) { }
    }
}

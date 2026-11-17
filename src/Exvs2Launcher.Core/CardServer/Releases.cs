using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Exvs2Launcher.Core.CardServer;

public sealed record ReleaseAsset(string Name, string DownloadUrl, long Size, string? Digest);

public sealed record ReleaseInfo(string Tag, string Name, bool Prerelease, bool Draft, DateTimeOffset? PublishedAt,
    string HtmlUrl, IReadOnlyList<ReleaseAsset> Assets, string? Body = null)
{
    public string Label => Tag + (Prerelease ? " (pre-release)" : "");
}

public interface IReleaseSource
{
    Task<IReadOnlyList<ReleaseInfo>> GetReleasesAsync(CancellationToken ct);
}

/// <summary>Reads the public release list of ServerOmega/exvs2-matchmaking from the GitHub API (no login).</summary>
public sealed class GitHubReleaseSource : IReleaseSource
{
    public const string Owner = "ServerOmega";
    public const string Repo = "exvs2-matchmaking";
    public static readonly string ApiUrl = $"https://api.github.com/repos/{Owner}/{Repo}/releases?per_page=30";
    public static readonly string ReleasesPage = $"https://github.com/{Owner}/{Repo}/releases";

    private readonly HttpClient _http;
    private readonly string _url;

    public GitHubReleaseSource(HttpClient http, string? url = null)
    {
        _http = http;
        _url = url ?? ApiUrl;
    }

    public async Task<IReadOnlyList<ReleaseInfo>> GetReleasesAsync(CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, _url);
        req.Headers.UserAgent.ParseAdd("exvs2-launcher");
        req.Headers.Accept.ParseAdd("application/vnd.github+json");
        using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException($"GitHub answered HTTP {(int)resp.StatusCode} for the release list. Try again later.");
        var json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        return ReleaseFeed.Parse(json);
    }
}

public static class ReleaseFeed
{
    /// <summary>Parses the GitHub "list releases" JSON. Unknown / broken entries are skipped.</summary>
    public static IReadOnlyList<ReleaseInfo> Parse(string json)
    {
        var list = new List<ReleaseInfo>();
        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.ValueKind != JsonValueKind.Array) return list;
        foreach (var r in doc.RootElement.EnumerateArray())
        {
            var tag = Str(r, "tag_name");
            if (string.IsNullOrWhiteSpace(tag)) continue;
            var assets = new List<ReleaseAsset>();
            if (r.TryGetProperty("assets", out var a) && a.ValueKind == JsonValueKind.Array)
                foreach (var x in a.EnumerateArray())
                {
                    var name = Str(x, "name");
                    var url = Str(x, "browser_download_url");
                    if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(url)) continue;
                    long size = x.TryGetProperty("size", out var s) && s.TryGetInt64(out var sz) ? sz : 0;
                    assets.Add(new ReleaseAsset(name, url, size, Str(x, "digest")));
                }
            DateTimeOffset? published = DateTimeOffset.TryParse(Str(r, "published_at"), CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal, out var p) ? p : null;
            list.Add(new ReleaseInfo(tag, Str(r, "name") ?? tag, Bool(r, "prerelease"), Bool(r, "draft"), published,
                Str(r, "html_url") ?? "", assets, Str(r, "body")));
        }
        return list;
    }

    private static string? Str(JsonElement e, string n) =>
        e.TryGetProperty(n, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;

    private static bool Bool(JsonElement e, string n) =>
        e.TryGetProperty(n, out var p) && p.ValueKind == JsonValueKind.True;
}

/// <summary>"v0.5.0", "v0.4.1-pre", "0.6.0-rc.1" style tags, compared like SemVer (a pre-release sorts before its release).</summary>
public sealed record ReleaseVersion(int Major, int Minor, int Patch, string Pre) : IComparable<ReleaseVersion>
{
    private static readonly Regex Rx = new(@"^v?(\d+)\.(\d+)(?:\.(\d+))?(?:-([0-9A-Za-z.\-]+))?$", RegexOptions.CultureInvariant);

    public static ReleaseVersion? TryParse(string? tag)
    {
        var m = Rx.Match((tag ?? "").Trim());
        if (!m.Success) return null;
        return new ReleaseVersion(int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture),
            int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture),
            m.Groups[3].Success ? int.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture) : 0,
            m.Groups[4].Success ? m.Groups[4].Value : "");
    }

    public int CompareTo(ReleaseVersion? other)
    {
        if (other is null) return 1;
        int c = Major.CompareTo(other.Major);
        if (c == 0) c = Minor.CompareTo(other.Minor);
        if (c == 0) c = Patch.CompareTo(other.Patch);
        if (c != 0) return c;
        if (Pre.Length == 0 || other.Pre.Length == 0) return (Pre.Length == 0 ? 1 : 0) - (other.Pre.Length == 0 ? 1 : 0);
        return string.CompareOrdinal(Pre, other.Pre);
    }

    /// <summary>True when <paramref name="candidate"/> is newer than <paramref name="installed"/> (unknown installed = newer).</summary>
    public static bool IsNewer(string? installed, string candidate)
    {
        var c = TryParse(candidate);
        if (c is null) return false;
        var i = TryParse(installed);
        return i is null || c.CompareTo(i) > 0;
    }
}

public enum PackageKind
{
    /// <summary>A ready-to-run Windows card server zip (Server-Infinite.exe inside).</summary>
    Prebuilt,
    /// <summary>The exvs2-matchmaking package (scripts + patch). Its own build script builds the server on this PC.</summary>
    BuildPackage,
}

/// <summary>What the launcher would download from one release.</summary>
public sealed record PackageChoice(ReleaseInfo Release, ReleaseAsset Zip, ReleaseAsset? ShaFile, PackageKind Kind);

public static class PackageSelector
{
    private static readonly Regex PrebuiltName = new(@"(win-?x64|windows).*\.zip$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>
    /// Picks the asset to install from a release: a prebuilt Windows zip if the release ever has one, otherwise the
    /// <c>exvs2-matchmaking-&lt;tag&gt;.zip</c> package. Needs a SHA-256 for it (a <c>.sha256</c> asset or GitHub's
    /// asset digest); without one the release is skipped.
    /// </summary>
    public static PackageChoice? Choose(ReleaseInfo r)
    {
        if (r.Draft) return null;
        var zips = r.Assets.Where(a => a.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)).ToList();
        var pre = zips.FirstOrDefault(a => PrebuiltName.IsMatch(a.Name));
        var pkg = zips.FirstOrDefault(a => a.Name.Equals($"exvs2-matchmaking-{r.Tag}.zip", StringComparison.OrdinalIgnoreCase))
                  ?? zips.FirstOrDefault(a => a.Name.StartsWith("exvs2-matchmaking-", StringComparison.OrdinalIgnoreCase) && !PrebuiltName.IsMatch(a.Name));
        var zip = pre ?? pkg;
        if (zip is null) return null;
        var sha = r.Assets.FirstOrDefault(a => a.Name.Equals(zip.Name + ".sha256", StringComparison.OrdinalIgnoreCase));
        if (sha is null && Sha256.FromDigest(zip.Digest) is null) return null;
        return new PackageChoice(r, zip, sha, pre is not null ? PackageKind.Prebuilt : PackageKind.BuildPackage);
    }

    /// <summary>The newest installable release (pre-releases only when <paramref name="includePrereleases"/>).</summary>
    public static PackageChoice? Latest(IEnumerable<ReleaseInfo> releases, bool includePrereleases) =>
        releases.Where(r => !r.Draft && (includePrereleases || !r.Prerelease))
            .Select(r => (r, v: ReleaseVersion.TryParse(r.Tag)))
            .Where(x => x.v is not null)
            .OrderByDescending(x => x.v)
            .Select(x => Choose(x.r))
            .FirstOrDefault(c => c is not null);
}

using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace Exvs2Launcher.Core.CardServer;

public static class Sha256
{
    private static readonly Regex Hex64 = new("^[0-9a-fA-F]{64}$", RegexOptions.CultureInvariant);

    /// <summary>"&lt;hex&gt;  name" or "&lt;hex&gt;" (sha256sum style; for <paramref name="fileName"/> when several lines).</summary>
    public static string? ParseShaFile(string? text, string? fileName = null)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        string? first = null;
        foreach (var raw in text.Split('\n'))
        {
            var parts = raw.Trim().Split((char[]?)null, 2, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0 || !Hex64.IsMatch(parts[0])) continue;
            var hex = parts[0].ToLowerInvariant();
            first ??= hex;
            if (fileName is not null && parts.Length == 2 && parts[1].TrimStart('*').Trim() == fileName) return hex;
        }
        return first;
    }

    /// <summary>GitHub's asset digest "sha256:&lt;hex&gt;" → hex.</summary>
    public static string? FromDigest(string? digest)
    {
        if (string.IsNullOrWhiteSpace(digest)) return null;
        var d = digest.Trim();
        if (!d.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase)) return null;
        var hex = d[7..];
        return Hex64.IsMatch(hex) ? hex.ToLowerInvariant() : null;
    }

    public static async Task<string> ComputeAsync(string path, CancellationToken ct = default)
    {
        await using var s = File.OpenRead(path);
        var hash = await SHA256.HashDataAsync(s, ct).ConfigureAwait(false);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    /// <summary>
    /// Every expected value that is known must match <paramref name="actual"/>, and at least one must be known.
    /// Returns null when it verifies, otherwise the reason.
    /// </summary>
    public static string? Verify(string actual, string? fromShaFile, string? fromDigest)
    {
        if (fromShaFile is null && fromDigest is null) return "The release has no SHA-256 for this download, so it was not used.";
        if (fromShaFile is not null && fromDigest is not null && fromShaFile != fromDigest)
            return "The release's .sha256 file and GitHub's checksum disagree. Nothing was installed.";
        var expected = fromShaFile ?? fromDigest!;
        return string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase)
            ? null
            : $"Checksum mismatch: expected {expected[..12]}…, got {actual[..12]}…. The download is damaged or was changed. Nothing was installed.";
    }
}

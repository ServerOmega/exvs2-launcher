using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Exvs2Launcher.Core.Files;
using Exvs2Launcher.Core.Identity;

namespace Exvs2Launcher.Core.Wizard;

/// <summary>Public (non-secret) view of a card server's Configurations/server.json.</summary>
public sealed record CardServerPublicConfig(string Path, string? MatchUrl, bool HasTagSecret, string? StunHost);

/// <summary>
/// Finds and updates Configurations/server.json. The online tag secret is written only to that file
/// and is never returned or included in a WriteReport note in clear text.
/// </summary>
public static class CardServerConfig
{
    public const string ReleaseDownloadUrl = "https://github.com/ServerOmega/exvs2-matchmaking/releases";
    public const string SetupA8Url = "https://github.com/ServerOmega/exvs2-matchmaking/blob/main/docs/SETUP.md#a8-online-tag-shared-secret-tagsecret";

    public static string? FindServerJson(string? cardServerPath)
    {
        foreach (var candidate in CandidateJsonPaths(cardServerPath))
        {
            if (File.Exists(candidate)) return candidate;
        }
        return null;
    }

    public static IEnumerable<string> CandidateJsonPaths(string? cardServerPath)
    {
        if (string.IsNullOrWhiteSpace(cardServerPath)) yield break;
        string start;
        try
        {
            var full = Path.GetFullPath(cardServerPath.Trim());
            start = File.Exists(full) ? Path.GetDirectoryName(full)! : full;
        }
        catch (Exception) { yield break; }

        var dirs = new List<string>();
        var cur = start;
        for (int i = 0; i < 5 && !string.IsNullOrEmpty(cur); i++)
        {
            dirs.Add(cur);
            foreach (var child in new[] { "server", "publish", "out", "win-x64" })
                dirs.Add(Path.Combine(cur, child));
            try { cur = Directory.GetParent(cur)?.FullName ?? ""; }
            catch (Exception) { break; }
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var dir in dirs)
        {
            if (!seen.Add(dir)) continue;
            yield return Path.Combine(dir, "Configurations", "server.json");
            yield return Path.Combine(dir, "server", "Configurations", "server.json");
        }
    }

    public static string? FindPatchedExe(string? cardServerPath)
    {
        if (string.IsNullOrWhiteSpace(cardServerPath)) return null;
        try
        {
            var full = Path.GetFullPath(cardServerPath.Trim());
            if (full.EndsWith("Server-Infinite.exe", StringComparison.OrdinalIgnoreCase) && File.Exists(full))
                return full;
            var dir = File.Exists(full) ? Path.GetDirectoryName(full)! : full;
            foreach (var cand in new[]
                     {
                         Path.Combine(dir, "Server-Infinite.exe"),
                         Path.Combine(dir, "server", "Server-Infinite.exe"),
                         Path.Combine(dir, "publish", "Server-Infinite.exe"),
                         Path.Combine(dir, "win-x64", "Server-Infinite.exe"),
                     })
            {
                if (File.Exists(cand)) return cand;
            }
            var parent = Directory.GetParent(dir)?.FullName;
            if (parent is not null)
            {
                var up = Path.Combine(parent, "server", "Server-Infinite.exe");
                if (File.Exists(up)) return up;
            }
        }
        catch (Exception) { }
        return null;
    }

    public static CardServerPublicConfig? ReadPublic(string? jsonPath)
    {
        if (string.IsNullOrWhiteSpace(jsonPath) || !File.Exists(jsonPath)) return null;
        try
        {
            var node = Parse(File.ReadAllText(jsonPath));
            if (node is null) return null;
            var match = node["OnlineMatching"]?["MatchUrl"]?.GetValue<string>();
            var stun = node["OnlineMatching"]?["StunHost"]?.GetValue<string>();
            var secret = node["OnlineTag"]?["TagSecret"]?.GetValue<string>();
            return new CardServerPublicConfig(jsonPath, EmptyToNull(match), !string.IsNullOrWhiteSpace(secret), EmptyToNull(stun));
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// Accepts a full match URL or a host[:port], and returns a normalized http://host:port/match plus the host
    /// for StunHost. Never throws.
    /// </summary>
    public static bool TryNormalizeMatchUrl(string? input, out string? url, out string? host, out string? error)
    {
        url = null;
        host = null;
        error = null;
        if (string.IsNullOrWhiteSpace(input))
        {
            error = "Enter the matchmaker address (for example 203.0.113.10:28080 or http://host:28080/match).";
            return false;
        }

        var raw = input.Trim();
        if (!raw.Contains("://", StringComparison.Ordinal))
            raw = "http://" + raw;

        if (!Uri.TryCreate(raw, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) ||
            string.IsNullOrEmpty(uri.Host) ||
            uri.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase) ||
            LoopbackIsPublicMistake(uri.Host))
        {
            if (Uri.TryCreate(raw, UriKind.Absolute, out var bad) && LoopbackIsPublicMistake(bad.Host))
            {
                error = "The matchmaker address should be the public host or LAN IP of the matchmaking server, not 127.0.0.1.";
                return false;
            }
            error = "That does not look like a matchmaker address.";
            return false;
        }

        int port = uri.IsDefaultPort ? 28080 : uri.Port;
        if (port is <= 0 or > 65535)
        {
            error = "The matchmaker port is not valid.";
            return false;
        }

        var path = string.IsNullOrEmpty(uri.AbsolutePath) || uri.AbsolutePath == "/" ? "/match" : uri.AbsolutePath;
        if (!path.EndsWith("/match", StringComparison.OrdinalIgnoreCase) && path == "/")
            path = "/match";
        url = $"{uri.Scheme}://{uri.Host}:{port}{path}";
        host = uri.Host;
        return true;
    }

    /// <summary>Writes MatchUrl and optional TagSecret. An empty secret is left unchanged. Backs up first.</summary>
    public static WriteReport Apply(string jsonPath, string? matchUrl, string? tagSecret, BackupService? backups = null)
    {
        var report = new WriteReport();
        backups ??= new BackupService();
        if (!File.Exists(jsonPath))
        {
            report.Notes.Add("Card server config not found: " + jsonPath);
            return report;
        }

        string? url = null;
        string? stunHost = null;
        if (!string.IsNullOrWhiteSpace(matchUrl))
        {
            if (!TryNormalizeMatchUrl(matchUrl, out url, out stunHost, out var err))
            {
                report.Notes.Add(err ?? "Matchmaker address was not valid; server.json was not changed.");
                return report;
            }
        }

        bool writeSecret = !string.IsNullOrWhiteSpace(tagSecret);
        var existing = ReadPublic(jsonPath);
        bool urlSame = url is null || string.Equals(existing?.MatchUrl, url, StringComparison.Ordinal);
        bool secretSame = !writeSecret; // if not writing, treat as same
        if (urlSame && secretSame && (stunHost is null || string.Equals(existing?.StunHost, stunHost, StringComparison.Ordinal)))
        {
            report.Unchanged.Add(jsonPath);
            report.Notes.Add("Card server config already has the requested matchmaker settings.");
            return report;
        }

        JsonNode? root;
        try { root = Parse(File.ReadAllText(jsonPath)); }
        catch (Exception ex)
        {
            report.Notes.Add("Could not read server.json: " + ex.Message);
            return report;
        }
        if (root is not JsonObject obj)
        {
            report.Notes.Add("server.json is not a JSON object; it was not changed.");
            return report;
        }

        if (url is not null)
        {
            var online = obj["OnlineMatching"] as JsonObject ?? new JsonObject();
            obj["OnlineMatching"] = online;
            online["MatchUrl"] = url;
            if (stunHost is not null) online["StunHost"] = stunHost;
        }

        if (writeSecret)
        {
            var tag = obj["OnlineTag"] as JsonObject ?? new JsonObject();
            obj["OnlineTag"] = tag;
            tag["TagSecret"] = tagSecret;
        }

        var bak = backups.Backup(jsonPath);
        if (bak is not null) report.Backups.Add(bak);

        var opts = new JsonSerializerOptions { WriteIndented = true };
        File.WriteAllText(jsonPath, obj.ToJsonString(opts) + Environment.NewLine, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        report.Written.Add(jsonPath);
        if (url is not null)
            report.Notes.Add("Updated OnlineMatching.MatchUrl to " + url + (stunHost is null ? "" : " and StunHost to " + stunHost) + ".");
        if (writeSecret)
            report.Notes.Add("Updated OnlineTag.TagSecret " + Secrets.Mask(tagSecret) + ". The value is stored only in the card server config and is never logged.");
        return report;
    }

    private static bool LoopbackIsPublicMistake(string host) =>
        host.Equals("localhost", StringComparison.OrdinalIgnoreCase) ||
        (IPAddress.TryParse(host, out var a) && IPAddress.IsLoopback(a));

    private static string? EmptyToNull(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    private static JsonNode? Parse(string text)
    {
        var opts = new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };
        return JsonNode.Parse(text, documentOptions: opts);
    }
}

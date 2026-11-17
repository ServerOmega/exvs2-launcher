using System.Text.Json;

namespace Exvs2Launcher.Core.Matchmaking;

/// <summary>
/// The matchmaker's public <c>GET /status</c> reply (totals only, no ids or addresses):
/// <c>{"service","version","online","searching","inMatch","matches","updatedAt","cacheSeconds"}</c>.
/// </summary>
public sealed record MatchmakerStatusInfo(string? Service, string? Version, int Online, int Searching, int InMatch,
    int Matches, DateTimeOffset? UpdatedAt)
{
    /// <summary>Parses a /status body; null when it is not one (older server, a web page, garbage).</summary>
    public static MatchmakerStatusInfo? TryParse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json) || json.Length > 64 * 1024) return null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            var r = doc.RootElement;
            if (r.ValueKind != JsonValueKind.Object) return null;
            if (!TryInt(r, "online", out var online) || !TryInt(r, "searching", out var searching) ||
                !TryInt(r, "inMatch", out var inMatch)) return null;
            TryInt(r, "matches", out var matches);
            DateTimeOffset? at = null;
            if (r.TryGetProperty("updatedAt", out var u) && u.ValueKind == JsonValueKind.String &&
                DateTimeOffset.TryParse(u.GetString(), System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.AssumeUniversal, out var parsed)) at = parsed;
            return new MatchmakerStatusInfo(Str(r, "service"), Str(r, "version"), online, searching, inMatch, matches, at);
        }
        catch (JsonException) { return null; }
    }

    private static bool TryInt(JsonElement r, string name, out int value)
    {
        value = 0;
        if (!r.TryGetProperty(name, out var p) || p.ValueKind != JsonValueKind.Number || !p.TryGetInt32(out value)) return false;
        value = Math.Max(0, value);
        return true;
    }

    private static string? Str(JsonElement r, string name)
    {
        if (!r.TryGetProperty(name, out var p) || p.ValueKind != JsonValueKind.String) return null;
        var s = p.GetString();
        return s is null ? null : (s.Length > 40 ? s[..40] : s);
    }

    public string Players => $"{Online} online ({Searching} searching, {InMatch} in match)";
}

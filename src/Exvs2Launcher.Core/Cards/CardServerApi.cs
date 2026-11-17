using System.Globalization;
using System.Net;
using System.Text.Json;
using Exvs2Launcher.Core.Identity;

namespace Exvs2Launcher.Core.Cards;

/// <summary>One card in the card server's public list. The server only returns a short chip id hint, never the full keys.</summary>
public sealed record CardSummary(int CardId, string UserName, string ChipIdHint)
{
    public string MaskedId => string.IsNullOrWhiteSpace(ChipIdHint) ? "(hidden)" : "…" + ChipIdHint;
}

public sealed record RecentBattle(DateTimeOffset? PlayedAt, string Mode, uint SuitId, bool Win, uint Seconds, uint Score,
    string? Teammate, IReadOnlyList<string> Opponents)
{
    public string Result => Win ? "Win" : "Loss";
    public string When => PlayedAt?.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) ?? "?";
    public string Duration => Seconds > 0 ? $"{Seconds / 60}:{Seconds % 60:00}" : "";
    public string Players => (Teammate is null ? "" : "with " + Teammate + "  ") + (Opponents.Count > 0 ? "vs " + string.Join(", ", Opponents) : "");
}

public sealed record CardStats(int Battles, int Wins, int Losses, DateTimeOffset? LastPlayed, IReadOnlyList<RecentBattle> Recent)
{
    public double WinRate => Battles == 0 ? 0 : 100.0 * Wins / Battles;
}

public sealed record LeaderboardRow(int Rank, string? Name, bool Hidden, uint ClassId, uint Grade, float Rate, uint Wins, uint Losses,
    uint Matches, double WinRate)
{
    public string DisplayName => Hidden || string.IsNullOrWhiteSpace(Name) ? "(hidden)" : Name!;
    public string ClassName => LeaderboardBoards.ClassName(ClassId);
}

public sealed record LeaderboardBoard(bool Available, string? Message, string Board, string Region, int MinMatches, int Ranked,
    DateTimeOffset? Updated, IReadOnlyList<LeaderboardRow> Rows, string Source);

public static class LeaderboardBoards
{
    public const string ClassSolo = "class-solo";
    public const string ClassTeam = "class-team";
    public const string Mix = "mix";
    public static readonly string[] All = { ClassSolo, ClassTeam, Mix };

    public static string Label(string board) => board switch
    {
        ClassSolo => "Class (solo)",
        ClassTeam => "Class (team)",
        Mix => "Mix",
        _ => board,
    };

    /// <summary>Infinite Boost class ranks as shown in the game.</summary>
    public static string ClassName(uint id) => id switch
    {
        1 => "Pilot", 2 => "Valiant", 3 => "Ace", 4 => "Extreme", _ => "-",
    };

    /// <summary>The cab's row on a board, matched by the card's display name (null when hidden or not ranked).</summary>
    public static LeaderboardRow? FindByName(LeaderboardBoard board, string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        return board.Rows.FirstOrDefault(r => !r.Hidden && string.Equals(r.Name?.Trim(), name.Trim(), StringComparison.Ordinal));
    }
}

/// <summary>
/// Read-only client for the patched card server's web API (the same routes its web page uses):
/// <c>/ui/card/getAll</c>, <c>/ui/card/getBasicDisplayProfile</c>, <c>/ui/card-stats/get-card-stats</c>,
/// <c>/ui/battle-history/get-recent-battle-histories</c> and <c>/ui/leaderboard/board/{board}</c>; plus the
/// matchmaker's public <c>/leaderboard/data</c>. Card keys are only ever sent to a loopback or LAN card server.
/// Nothing here writes to the server or its database.
/// </summary>
public sealed class CardServerApi
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };
    private readonly HttpClient _http;
    private readonly Uri _base;

    public CardServerApi(HttpClient http, string baseUrl)
    {
        _http = http;
        if (!Uri.TryCreate(baseUrl.TrimEnd('/') + "/", UriKind.Absolute, out var u) || (u.Scheme != "http" && u.Scheme != "https"))
            throw new ArgumentException("Card server address is not a valid http URL: " + baseUrl, nameof(baseUrl));
        _base = u;
    }

    public Uri BaseUri => _base;

    /// <summary>True for loopback and private LAN hosts: the only places card keys are sent.</summary>
    public static bool IsLocalHost(Uri u)
    {
        if (u.IsLoopback) return true;
        var host = u.Host.Trim('[', ']');
        if (host.Equals("localhost", StringComparison.OrdinalIgnoreCase)) return true;
        return IPAddress.TryParse(host, out var ip) && ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork &&
               !Diagnostics.Redactor.IsPublicIpv4(host);
    }

    public async Task<IReadOnlyList<CardSummary>> GetCardsAsync(string? search = null, int pageSize = 100, CancellationToken ct = default)
    {
        var list = new List<CardSummary>();
        int requested = pageSize;
        for (int page = 1; page <= 50; page++)
        {
            var q = $"ui/card/getAll?Page={page}&PageSize={requested}" + (string.IsNullOrWhiteSpace(search) ? "" : "&SearchKeyword=" + Uri.EscapeDataString(search.Trim()));
            var json = await GetAsync(q, ct).ConfigureAwait(false);
            var batch = ParseCards(json);
            // The server may cap the page size; stop on an empty page or a page with nothing new.
            var fresh = batch.Where(c => !list.Any(x => x.CardId == c.CardId)).ToList();
            if (fresh.Count == 0) break;
            list.AddRange(fresh);
            if (page == 1 && batch.Count < pageSize) pageSize = Math.Max(1, batch.Count);
            if (batch.Count < pageSize) break;
        }
        return list;
    }

    public async Task<string?> GetDisplayNameAsync(CardIdentity card, CancellationToken ct = default)
    {
        var json = await GetAsync(KeyPath("ui/card/getBasicDisplayProfile", card), ct).ConfigureAwait(false);
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.ValueKind == JsonValueKind.Object && TryProp(doc.RootElement, "userName", out var n) && n.ValueKind == JsonValueKind.String
            ? n.GetString() : null;
    }

    /// <summary>Play count, wins / losses and last played for one card (needs a card server with the Stats page).</summary>
    public async Task<CardStats> GetCardStatsAsync(CardIdentity card, CancellationToken ct = default)
        => ParseCardStats(await GetAsync(KeyPath("ui/card-stats/get-card-stats", card), ct).ConfigureAwait(false));

    public async Task<IReadOnlyList<RecentBattle>> GetRecentBattlesAsync(CardIdentity card, CancellationToken ct = default)
        => ParseBattleHistory(await GetAsync(KeyPath("ui/battle-history/get-recent-battle-histories", card), ct).ConfigureAwait(false));

    public async Task<LeaderboardBoard> GetBoardAsync(string board, CancellationToken ct = default)
        => ParseBoard(await GetAsync("ui/leaderboard/board/" + Uri.EscapeDataString(board), ct).ConfigureAwait(false), "card server");

    /// <summary>The matchmaker's public board (<c>http://host:port/leaderboard/data?board=</c>). No card data is sent.</summary>
    public static async Task<LeaderboardBoard> GetMatchmakerBoardAsync(HttpClient http, string host, int port, string board, CancellationToken ct = default)
    {
        var h = host.Trim();
        if (System.Net.IPAddress.TryParse(h.Trim('[', ']'), out var ip) && ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6)
            h = "[" + h.Trim('[', ']') + "]";
        var url = $"http://{h}:{port}/leaderboard/data?board={Uri.EscapeDataString(board)}&limit=100";
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        using var resp = await http.SendAsync(req, ct).ConfigureAwait(false);
        if (resp.StatusCode == HttpStatusCode.NotFound)
            return new LeaderboardBoard(false, "This matchmaker does not publish leaderboards.", board, "", 0, 0, null, Array.Empty<LeaderboardRow>(), "matchmaker");
        resp.EnsureSuccessStatusCode();
        var json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        var b = ParseBoard(json, "matchmaker");
        return b with { Available = true };
    }

    private string KeyPath(string route, CardIdentity card)
    {
        if (!IsLocalHost(_base))
            throw new InvalidOperationException("Card keys are only sent to a card server on this PC or LAN. " + _base.Host + " is not local.");
        if (!IdentityFormat.IsValidAccessCode(card.AccessCode) || !IdentityFormat.IsValidChipId(card.ChipId))
            throw new InvalidOperationException("This cab's card.ini does not have a valid card.");
        return route + "/" + Uri.EscapeDataString(IdentityFormat.NormalizeIniValue(card.AccessCode)) + "/" +
               Uri.EscapeDataString(IdentityFormat.NormalizeIniValue(card.ChipId));
    }

    private async Task<string> GetAsync(string relative, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(10));
        using var req = new HttpRequestMessage(HttpMethod.Get, new Uri(_base, relative));
        using var resp = await _http.SendAsync(req, cts.Token).ConfigureAwait(false);
        if (resp.StatusCode == HttpStatusCode.NotFound)
            throw new InvalidOperationException("The card server does not have this page (HTTP 404). It may be an older build.");
        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException($"The card server answered HTTP {(int)resp.StatusCode}.");
        var s = await resp.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(false);
        if (s.Length > 8 * 1024 * 1024) throw new InvalidOperationException("The card server reply is too large.");
        return s;
    }

    // ---------- parsing (public for tests) ----------

    public static IReadOnlyList<CardSummary> ParseCards(string json)
    {
        var list = new List<CardSummary>();
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (root.ValueKind == JsonValueKind.Object && TryProp(root, "data", out var data)) root = data;
        if (root.ValueKind != JsonValueKind.Array) return list;
        foreach (var e in root.EnumerateArray())
        {
            if (e.ValueKind != JsonValueKind.Object) continue;
            int id = TryProp(e, "cardId", out var i) && i.TryGetInt32(out var iv) ? iv : 0;
            list.Add(new CardSummary(id, Str(e, "userName") ?? "", Str(e, "chipIdHint") ?? ""));
        }
        return list;
    }

    public static CardStats ParseCardStats(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var r = doc.RootElement;
        int wins = 0, losses = 0;
        bool sawTotal = false;
        if (TryProp(r, "modeRecords", out var modes) && modes.ValueKind == JsonValueKind.Array)
        {
            foreach (var m in modes.EnumerateArray())
            {
                var mode = Str(m, "mode") ?? "";
                int w = Int(m, "wins"), l = Int(m, "losses");
                if (mode == "Total") { wins = w; losses = l; sawTotal = true; break; }
                wins += w; losses += l;
            }
        }
        var recent = new List<RecentBattle>();
        if (TryProp(r, "recentBattles", out var rb) && rb.ValueKind == JsonValueKind.Array)
            foreach (var b in rb.EnumerateArray())
                recent.Add(new RecentBattle(UnixTime(b, "playedAt"), Str(b, "mode") ?? "", (uint)Int(b, "mobileSuitId"),
                    Bool(b, "isWin"), (uint)Int(b, "elapsedSeconds"), (uint)Int(b, "score"), null, Array.Empty<string>()));
        int battles = wins + losses;
        if (!sawTotal && battles == 0) battles = Int(r, "historyBattleCount");
        var last = recent.Where(x => x.PlayedAt is not null).Select(x => x.PlayedAt).Max();
        return new CardStats(battles, wins, losses, last, recent);
    }

    public static IReadOnlyList<RecentBattle> ParseBattleHistory(string json)
    {
        var list = new List<RecentBattle>();
        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.ValueKind != JsonValueKind.Array) return list;
        foreach (var b in doc.RootElement.EnumerateArray())
        {
            uint suit = 0;
            if (TryProp(b, "selfPlayer", out var self) && self.ValueKind == JsonValueKind.Object) suit = (uint)Int(self, "mobileSuitId");
            string? mate = TryProp(b, "teammate", out var t) && t.ValueKind == JsonValueKind.Object ? Str(t, "playerName") : null;
            var opps = new List<string>();
            if (TryProp(b, "opponents", out var o) && o.ValueKind == JsonValueKind.Array)
                foreach (var x in o.EnumerateArray()) if (Str(x, "playerName") is string n && n.Length > 0) opps.Add(n);
            list.Add(new RecentBattle(UnixTime(b, "playedAt"), "", suit, Bool(b, "isWin"), (uint)Int(b, "elapsedSeconds"),
                (uint)Int(b, "score"), mate, opps));
        }
        return list;
    }

    public static LeaderboardBoard ParseBoard(string json, string source)
    {
        using var doc = JsonDocument.Parse(json);
        var r = doc.RootElement;
        var rows = new List<LeaderboardRow>();
        if (TryProp(r, "rows", out var arr) && arr.ValueKind == JsonValueKind.Array)
            foreach (var x in arr.EnumerateArray())
                rows.Add(new LeaderboardRow(Int(x, "rank"), Str(x, "name"), Bool(x, "hidden"), (uint)Int(x, "classId"), (uint)Int(x, "grade"),
                    (float)Dbl(x, "rate"), (uint)Int(x, "wins"), (uint)Int(x, "losses"), (uint)Int(x, "matches"), Dbl(x, "winRate")));
        DateTimeOffset? updated = DateTimeOffset.TryParse(Str(r, "updatedUtc"), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var u) ? u : null;
        bool available = TryProp(r, "available", out var a) ? a.ValueKind == JsonValueKind.True : rows.Count > 0;
        return new LeaderboardBoard(available, Str(r, "message"), Str(r, "board") ?? "", Str(r, "region") ?? "", Int(r, "minMatches"),
            Int(r, "ranked"), updated, rows, source);
    }

    /// <summary>Card server times are Unix seconds; milliseconds are accepted too.</summary>
    internal static DateTimeOffset? UnixTime(JsonElement e, string name)
    {
        if (!TryProp(e, name, out var p) || p.ValueKind != JsonValueKind.Number || !p.TryGetUInt64(out var v) || v == 0) return null;
        try { return v > 100_000_000_000UL ? DateTimeOffset.FromUnixTimeMilliseconds((long)v) : DateTimeOffset.FromUnixTimeSeconds((long)v); }
        catch (ArgumentOutOfRangeException) { return null; }
    }

    private static bool TryProp(JsonElement e, string name, out JsonElement value)
    {
        value = default;
        if (e.ValueKind != JsonValueKind.Object) return false;
        foreach (var p in e.EnumerateObject())
            if (string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)) { value = p.Value; return true; }
        return false;
    }

    private static string? Str(JsonElement e, string n) => TryProp(e, n, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;
    private static bool Bool(JsonElement e, string n) => TryProp(e, n, out var p) && p.ValueKind == JsonValueKind.True;
    private static int Int(JsonElement e, string n) =>
        TryProp(e, n, out var p) && p.ValueKind == JsonValueKind.Number ? (p.TryGetInt32(out var i) ? i : p.TryGetInt64(out var l) ? (int)Math.Clamp(l, int.MinValue, int.MaxValue) : 0) : 0;
    private static double Dbl(JsonElement e, string n) => TryProp(e, n, out var p) && p.ValueKind == JsonValueKind.Number ? p.GetDouble() : 0;
}

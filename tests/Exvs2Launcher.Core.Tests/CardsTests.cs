using System.Net;
using Exvs2Launcher.Core.Cards;
using Exvs2Launcher.Core.Identity;

namespace Exvs2Launcher.Core.Tests;

internal sealed class StubHttp : HttpMessageHandler
{
    public List<Uri> Requests { get; } = new();
    public Func<Uri, (HttpStatusCode, string)> Reply { get; set; } = _ => (HttpStatusCode.OK, "[]");
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        Requests.Add(request.RequestUri!);
        var (code, body) = Reply(request.RequestUri!);
        return Task.FromResult(new HttpResponseMessage(code) { Content = new StringContent(body) });
    }
}

public class CardsTests
{
    private static readonly CardIdentity Card = new("12345678901234567890", "0123456789ABCDEF0123456789ABCDEF");

    [Fact]
    public void Parses_card_list_with_hints_only()
    {
        var cards = CardServerApi.ParseCards("""[{"cardId":3,"userName":"AMURO","chipId":"","chipIdHint":"CDEF"},{"cardId":4,"userName":"CHAR"}]""");
        Assert.Equal(2, cards.Count);
        Assert.Equal("…CDEF", cards[0].MaskedId);
        Assert.Equal("(hidden)", cards[1].MaskedId);
    }

    [Fact]
    public void Parses_card_stats_total_and_last_played()
    {
        var json = """
        {"modeRecords":[{"mode":"Total","wins":7,"losses":3},{"mode":"Mix","wins":2,"losses":1}],
         "recentBattles":[{"playedAt":1763300000,"mode":"Mix","mobileSuitId":12,"isWin":true,"elapsedSeconds":125,"score":900},
                          {"playedAt":1763200000,"mode":"ClassSolo","mobileSuitId":5,"isWin":false}],"historyBattleCount":2}
        """;
        var s = CardServerApi.ParseCardStats(json);
        Assert.Equal(10, s.Battles);
        Assert.Equal(7, s.Wins);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1763300000), s.LastPlayed);
        Assert.Equal("2:05", s.Recent[0].Duration);
        Assert.Equal(70, s.WinRate, 3);
    }

    [Fact]
    public void Parses_battle_history_and_leaderboard()
    {
        var h = CardServerApi.ParseBattleHistory("""
        [{"battleHistoryId":1,"playedAt":1763300000000,"isWin":false,"elapsedSeconds":60,"stageId":1,"score":10,"burstType":0,
          "selfPlayer":{"cardId":1,"playerName":"ME","mobileSuitId":44},"teammate":{"playerName":"BUDDY"},
          "opponents":[{"playerName":"A"},{"playerName":"B"}]}]
        """);
        var b = Assert.Single(h);
        Assert.Equal(44u, b.SuitId);
        Assert.Equal("with BUDDY  vs A, B", b.Players);
        Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(1763300000000), b.PlayedAt);

        var board = CardServerApi.ParseBoard("""
        {"available":true,"board":"class-solo","region":"LA","minMatches":5,"ranked":2,"updatedUtc":"2026-11-16T10:00:00Z",
         "rows":[{"rank":1,"name":"ACE","classId":4,"grade":3,"rate":1700.5,"wins":20,"losses":5,"matches":25,"winRate":80.0},
                 {"rank":2,"name":null,"hidden":true,"classId":2,"matches":9}]}
        """, "card server");
        Assert.True(board.Available);
        Assert.Equal("Extreme", board.Rows[0].ClassName);
        Assert.Equal("(hidden)", board.Rows[1].DisplayName);
        Assert.Equal(1, LeaderboardBoards.FindByName(board, "ACE")!.Rank);
        Assert.Null(LeaderboardBoards.FindByName(board, null));
    }

    [Fact]
    public async Task Card_keys_are_never_sent_to_a_public_host()
    {
        var h = new StubHttp();
        var api = new CardServerApi(new HttpClient(h), "http://203.0.113.5:28080");
        await Assert.ThrowsAsync<InvalidOperationException>(() => api.GetCardStatsAsync(Card));
        Assert.Empty(h.Requests);
    }

    [Fact]
    public async Task Local_card_server_gets_key_path_and_paging_stops()
    {
        var h = new StubHttp
        {
            Reply = u => u.AbsolutePath.Contains("getAll")
                ? (HttpStatusCode.OK, u.Query.Contains("Page=1&") ? """[{"cardId":1,"userName":"A"},{"cardId":2,"userName":"B"}]""" : "[]")
                : (HttpStatusCode.OK, """{"modeRecords":[]}"""),
        };
        var api = new CardServerApi(new HttpClient(h), "http://127.0.0.1:28080");
        var cards = await api.GetCardsAsync();
        Assert.Equal(2, cards.Count);
        // A short first page may just be the server's own page-size cap, so one more page is asked for; empty → done.
        Assert.Equal(2, h.Requests.Count);
        await api.GetCardStatsAsync(Card);
        Assert.EndsWith("/ui/card-stats/get-card-stats/12345678901234567890/0123456789ABCDEF0123456789ABCDEF", h.Requests[^1].AbsolutePath);
    }

    [Fact]
    public async Task Older_server_404_gives_readable_error()
    {
        var h = new StubHttp { Reply = _ => (HttpStatusCode.NotFound, "") };
        var api = new CardServerApi(new HttpClient(h), "http://localhost:28080");
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => api.GetBoardAsync("mix"));
        Assert.Contains("404", ex.Message);
    }

    [Fact]
    public void Card_ini_backup_and_restore_round_trip_with_safety_backup()
    {
        using var d = new Tmp();
        var game = Path.Combine(d.Path, "cab");
        d.File("cab/card.ini", "[card]\naccessCode=12345678901234567890\nchipId=0123456789ABCDEF0123456789ABCDEF\n");
        var t = new DateTime(2026, 11, 16, 10, 0, 0);
        var b = new CardIniBackup(Path.Combine(d.Path, "backups"), () => t);
        var saved = b.Backup("V29", game);
        Assert.True(File.Exists(saved));
        Assert.Equal(Card, CardIniBackup.ReadCard(game));

        File.WriteAllText(Path.Combine(game, "card.ini"), "[card]\naccessCode=99999999999999999999\nchipId=FFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFF\n");
        Assert.Throws<InvalidOperationException>(() => b.Restore(saved, game, cabRunning: true));
        t = t.AddMinutes(1);
        var replaced = b.Restore(saved, game, cabRunning: false);
        Assert.NotNull(replaced);
        Assert.Contains("99999999999999999999", File.ReadAllText(replaced!));
        Assert.Equal(Card, CardIniBackup.ReadCard(game));
        Assert.Single(b.List("V29"));
    }

    [Fact]
    public void Restore_refuses_a_backup_without_a_valid_card()
    {
        using var d = new Tmp();
        d.File("cab/card.ini", "[card]\naccessCode=12345678901234567890\nchipId=0123456789ABCDEF0123456789ABCDEF\n");
        var bad = d.File("bad.ini", "[card]\naccessCode=nope\n");
        var b = new CardIniBackup(Path.Combine(d.Path, "backups"));
        Assert.Throws<InvalidOperationException>(() => b.Restore(bad, Path.Combine(d.Path, "cab"), false));
        Assert.Contains("12345678901234567890", File.ReadAllText(Path.Combine(d.Path, "cab", "card.ini")));
    }
}

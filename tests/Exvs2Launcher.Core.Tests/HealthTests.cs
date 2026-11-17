using Exvs2Launcher.Core.Health;
using Exvs2Launcher.Core.Launch;
using Exvs2Launcher.Core.Profiles;

namespace Exvs2Launcher.Core.Tests;

public class HealthTests
{
    [Theory]
    [InlineData("2026-11-14 09:58:27.685 -08:00 [INF] MATCH room=1 mix-match S[1158] vs S[1156] (humans=2/4)", HealthEventKind.MatchFound)]
    [InlineData("2026-11-14 09:58:27.685 -08:00 [INF] ROOM-END room=1 mix node=5 serial=AB12 reason=timeout (no re-entry)", HealthEventKind.PossibleDisconnect)]
    [InlineData("2026-11-14 09:58:27.685 -08:00 [INF] ROOM-END room=1 mix node=5 serial=AB12 reason=cancel-after-match apply=3", HealthEventKind.PossibleDisconnect)]
    [InlineData("2026-11-14 09:58:27.685 -08:00 [INF] ROOM-END room=1 mix node=5 serial=AB12 reason=reentry mix apply=4", HealthEventKind.RoomEnded)]
    [InlineData("2026-11-14 09:58:27.685 -08:00 [WRN] STUB SaveFailedBattleLogOn pilot=1 room=2 my_fault=0 mode=3", HealthEventKind.FailedBattle)]
    [InlineData("2026-11-14 09:58:27.685 -08:00 [INF] SaveVsmOnResult stored CardId=1 mode=mix room=2 win=True", HealthEventKind.ResultSaved)]
    [InlineData("2026-11-14 09:58:27.685 -08:00 [ERR] Unhandled exception in handler", HealthEventKind.Error)]
    [InlineData("2026-11-14 09:58:27.685 -08:00 [INF] NET-WARN node=3 has no STUN global address", HealthEventKind.Warning)]
    [InlineData("2026-11-14 09:58:27.685 -08:00 [INF] cab showed error 21-01 after loading", HealthEventKind.PossibleDisconnect)]
    public void Classifies_server_lines(string line, HealthEventKind kind) => Assert.Equal(kind, LogClassifier.Classify(line)!.Kind);

    [Theory]
    [InlineData("2026-11-14 09:58:27.685 -08:00 [INF] Request is {...}")]
    [InlineData("  \"DisableTelop\": false,")]
    [InlineData("")]
    [InlineData("2026-11-14 09:58:27.685 -08:00 [INF] MATCH room=1 cpu-stand-ins: 3")]
    public void Ignores_ordinary_lines(string line) => Assert.Null(LogClassifier.Classify(line));

    [Fact]
    public void Tailer_starts_at_end_then_returns_only_new_complete_lines()
    {
        using var d = new Tmp();
        var log = d.File("Logs/log-20261116.txt", "old 1\nold 2\n");
        var t = LogTailer.ForFolder(Path.Combine(d.Path, "Logs"));
        Assert.Empty(t.Poll());
        Assert.Equal(new[] { "old 1", "old 2" }, t.Recent(5));
        File.AppendAllText(log, "new 1\nnew 2 partial");
        Assert.Equal(new[] { "new 1" }, t.Poll());
        File.AppendAllText(log, " done\n");
        Assert.Equal(new[] { "new 2 partial done" }, t.Poll());
    }

    [Fact]
    public void Tailer_follows_day_rollover()
    {
        using var d = new Tmp();
        d.File("Logs/log-20261116.txt", "a\n");
        var t = LogTailer.ForFolder(Path.Combine(d.Path, "Logs"));
        t.Poll();
        var next = d.File("Logs/log-20261117.txt", "first of day\n");
        File.SetLastWriteTimeUtc(next, DateTime.UtcNow.AddMinutes(1));
        Assert.Equal(new[] { "first of day" }, t.Poll());
    }

    [Fact]
    public void Monitor_reports_start_and_unexpected_exit_but_not_expected_stop()
    {
        var pids = new Dictionary<string, int?> { ["Client"] = null };
        var p = new LauncherProfile { Name = "P2", GameFolder = "/g/P2" };
        var m = new HealthMonitor((prof, role) => new ComponentStatus(role, role == "Client" && pids["Client"] is not null, role == "Client" ? pids["Client"] : null, null),
            _ => null, null, () => new DateTime(2026, 11, 16, 12, 0, 0));
        Assert.Empty(m.Poll(new[] { p }));
        pids["Client"] = 500;
        Assert.Equal(HealthEventKind.Started, Assert.Single(m.Poll(new[] { p })).Kind);
        pids["Client"] = null;
        var ev = Assert.Single(m.Poll(new[] { p }));
        Assert.Equal(HealthEventKind.StoppedUnexpectedly, ev.Kind);
        Assert.True(ev.Important);
        pids["Client"] = 501;
        m.Poll(new[] { p });
        m.ExpectStop("P2", ComponentRoles.Client);
        pids["Client"] = null;
        Assert.Equal(HealthEventKind.Exited, Assert.Single(m.Poll(new[] { p })).Kind);
    }

    [Fact]
    public void Monitor_tails_log_attributes_by_serial_and_saves_masked_context()
    {
        using var d = new Tmp();
        var log = d.File("srv/Logs/log-20261116.txt", "boot\n");
        var save = Path.Combine(d.Path, "events");
        var cabs = new[]
        {
            new LauncherProfile { Name = "V29", CardServerPath = "/srv/Server-Infinite.exe", Serial = "AB12" },
            new LauncherProfile { Name = "P2", CardServerPath = "/srv/Server-Infinite.exe", Serial = "CD34" },
        };
        var tailer = LogTailer.ForFolder(Path.Combine(d.Path, "srv", "Logs"));
        var m = new HealthMonitor((_, role) => new ComponentStatus(role, false, null, null), _ => tailer, save) { ContextLines = 5 };
        m.Poll(cabs);
        File.AppendAllText(log,
            "2026-11-16 10:00:00.000 -08:00 [INF] loaded card 12345678901234567890 from 8.8.8.8\n" +
            "2026-11-16 10:00:01.000 -08:00 [INF] ROOM-END room=4 mix node=9 serial=CD34 reason=timeout (no re-entry)\n" +
            "2026-11-16 10:00:02.000 -08:00 [INF] MATCH room=5 mix-match S[1] vs S[2] (humans=2/4)\n");
        var events = m.Poll(cabs);
        var drop = Assert.Single(events, e => e.Kind == HealthEventKind.PossibleDisconnect);
        Assert.Equal("P2", drop.Cab);
        Assert.NotNull(drop.SavedPath);
        var saved = File.ReadAllText(drop.SavedPath!);
        Assert.Contains("ROOM-END", saved);
        Assert.Contains("loaded card", saved);
        Assert.DoesNotContain("12345678901234567890", saved);
        Assert.DoesNotContain("8.8.8.8", saved);
        var match = Assert.Single(events, e => e.Kind == HealthEventKind.MatchFound);
        Assert.StartsWith("Card server (", match.Cab);
        Assert.Null(match.SavedPath);
    }

    [Fact]
    public void Warning_floods_are_collapsed()
    {
        using var d = new Tmp();
        var log = d.File("Logs/log-1.txt", "");
        var tailer = LogTailer.ForFolder(Path.Combine(d.Path, "Logs"));
        var p = new LauncherProfile { Name = "A", CardServerPath = "/x/Server-Infinite.exe" };
        var m = new HealthMonitor((_, role) => new ComponentStatus(role, false, null, null), _ => tailer);
        m.Poll(new[] { p });
        File.AppendAllText(log, string.Concat(Enumerable.Repeat("2026-11-16 10:00:00.000 -08:00 [WRN] same thing\n", 10)));
        Assert.Single(m.Poll(new[] { p }));
    }
}

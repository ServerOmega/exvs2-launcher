using System.Text;
using System.Text.Json.Nodes;
using Exvs2Launcher.Core.Health;
using Exvs2Launcher.Core.Matchmaking;
using Exvs2Launcher.Core.Notifications;
using Exvs2Launcher.Core.Presence;
using Exvs2Launcher.Core.Profiles;

namespace Exvs2Launcher.Core.Tests;

/// <summary>A fake Discord pipe: reads what the client writes, answers from a queue of frames.</summary>
internal sealed class FakeDiscordPipe : Stream
{
    private readonly MemoryStream _toClient = new();
    public MemoryStream FromClient { get; } = new();
    public FakeDiscordPipe(params (int Op, string Json)[] replies)
    {
        foreach (var (op, json) in replies) _toClient.Write(DiscordFrame.Encode(op, json));
        _toClient.Position = 0;
    }
    public override bool CanRead => true; public override bool CanSeek => false; public override bool CanWrite => true;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override void Flush() { }
    public override int Read(byte[] buffer, int offset, int count) => _toClient.Read(buffer, offset, count);
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => FromClient.Write(buffer, offset, count);
}

public class PresenceAndNotificationTests
{
    private const string AppId = "123456789012345678";

    [Fact]
    public async Task Frame_round_trip()
    {
        var bytes = DiscordFrame.Encode(DiscordFrame.Frame, "{\"a\":1}");
        Assert.Equal(new byte[] { 1, 0, 0, 0, 7, 0, 0, 0 }, bytes[..8]);
        var (op, json) = await DiscordFrame.ReadAsync(new MemoryStream(bytes), CancellationToken.None);
        Assert.Equal(1, op);
        Assert.Equal("{\"a\":1}", json);
    }

    [Fact]
    public async Task Handshake_then_set_activity()
    {
        var pipe = new FakeDiscordPipe((1, "{\"cmd\":\"DISPATCH\",\"evt\":\"READY\"}"), (1, "{\"cmd\":\"SET_ACTIVITY\",\"evt\":null}"));
        await using var c = new DiscordIpcClient(_ => Task.FromResult<Stream?>(pipe));
        Assert.True(await c.ConnectAsync(AppId, CancellationToken.None), c.LastError);
        var activity = PresenceTracker.Activity(PlayState.Match, 4, DateTimeOffset.FromUnixTimeSeconds(1000), inviteButton: true);
        Assert.True(await c.SetActivityAsync(activity, 42, CancellationToken.None), c.LastError);

        pipe.FromClient.Position = 0;
        var (op0, hello) = await DiscordFrame.ReadAsync(pipe.FromClient, CancellationToken.None);
        Assert.Equal(DiscordFrame.Handshake, op0);
        Assert.Contains(AppId, hello);
        var (op1, cmd) = await DiscordFrame.ReadAsync(pipe.FromClient, CancellationToken.None);
        Assert.Equal(DiscordFrame.Frame, op1);
        var n = JsonNode.Parse(cmd)!;
        Assert.Equal("SET_ACTIVITY", n["cmd"]!.GetValue<string>());
        Assert.Equal(42, n["args"]!["pid"]!.GetValue<int>());
        Assert.Equal("In match · 4 cabs", n["args"]!["activity"]!["state"]!.GetValue<string>());
        Assert.Equal(PresenceTracker.InviteUrl, n["args"]!["activity"]!["buttons"]![0]!["url"]!.GetValue<string>());
    }

    [Fact]
    public async Task Bad_client_id_or_no_discord_fails_cleanly()
    {
        await using var c = new DiscordIpcClient(_ => Task.FromResult<Stream?>(null));
        Assert.False(await c.ConnectAsync("", CancellationToken.None));
        Assert.False(await c.ConnectAsync("abc", CancellationToken.None));
        Assert.False(await c.ConnectAsync(AppId, CancellationToken.None));
        Assert.Contains("not running", c.LastError);
        var rejected = new FakeDiscordPipe((2, "{\"code\":4000,\"message\":\"Invalid Client ID\"}"));
        await using var c2 = new DiscordIpcClient(_ => Task.FromResult<Stream?>(rejected));
        Assert.False(await c2.ConnectAsync(AppId, CancellationToken.None));
        Assert.Contains("Invalid Client ID", c2.LastError);
    }

    [Fact]
    public void Clear_activity_sends_null()
    {
        var cmd = DiscordIpcClient.BuildSetActivity(null, 7, "n");
        Assert.Contains("\"activity\":null", cmd);
        Assert.Null(PresenceTracker.Activity(PlayState.NotPlaying, 0, null, true));
    }

    [Fact]
    public void Tracker_states_follow_log_events_and_time_out()
    {
        var t0 = new DateTime(2026, 11, 16, 12, 0, 0);
        var tr = new PresenceTracker();
        Assert.Equal(PlayState.NotPlaying, tr.State(false, t0));
        Assert.Equal(PlayState.Menus, tr.State(true, t0));
        tr.Observe(new HealthEvent(t0, "A", "log", HealthEventKind.QueueActivity, ""));
        Assert.Equal(PlayState.Queue, tr.State(true, t0.AddMinutes(1)));
        Assert.Equal(PlayState.Menus, tr.State(true, t0.AddMinutes(4)));
        tr.Observe(new HealthEvent(t0, "A", "log", HealthEventKind.MatchFound, ""));
        Assert.Equal(PlayState.Match, tr.State(true, t0.AddMinutes(2)));
        tr.Observe(new HealthEvent(t0, "A", "log", HealthEventKind.ResultSaved, ""));
        Assert.Equal(PlayState.Menus, tr.State(true, t0.AddMinutes(3)));
        tr.Observe(new HealthEvent(t0, "A", "log", HealthEventKind.MatchFound, ""));
        Assert.Equal(PlayState.Menus, tr.State(true, t0.AddMinutes(13)));
    }

    private static MatchmakerStatusInfo S(int searching, int inMatch, int matches) => new("mm", "1", searching + inMatch, searching, inMatch, matches, null);

    [Fact]
    public void Notifications_are_off_by_default_and_silent_on_first_look()
    {
        var rules = new NotificationRules();
        var defaults = new LauncherSettings();
        Assert.False(defaults.NotifySound || defaults.NotifyToast);
        Assert.False(defaults.RichPresenceEnabled);
        Assert.Equal("", defaults.RichPresenceClientId);
        Assert.Empty(rules.FromStatus("LA", S(0, 0, 0), S(1, 0, 0), defaults));
        var on = new LauncherSettings { NotifySound = true };
        Assert.Empty(rules.FromStatus("LA", null, S(3, 0, 0), on));
    }

    [Fact]
    public void Queue_then_match_notices_with_throttle()
    {
        var now = new DateTime(2026, 11, 16, 12, 0, 0);
        var rules = new NotificationRules(() => now);
        var s = new LauncherSettings { NotifyToast = true };
        var q = Assert.Single(rules.FromStatus("LA", S(0, 0, 0), S(1, 0, 0), s));
        Assert.Equal("queue", q.Kind);
        Assert.Empty(rules.FromStatus("LA", S(1, 0, 0), S(2, 0, 0), s)); // throttled
        var m = Assert.Single(rules.FromStatus("LA", S(2, 0, 0), S(0, 2, 1), s));
        Assert.Equal("match", m.Kind);
        now = now.AddMinutes(1);
        Assert.Single(rules.FromStatus("LA", S(0, 0, 0), S(1, 0, 1), s));
        var local = rules.FromHealth(new HealthEvent(now, "P2", "log", HealthEventKind.MatchFound, ""), s);
        Assert.Contains("P2", local!.Text);
        Assert.Null(rules.FromHealth(new HealthEvent(now, "P2", "log", HealthEventKind.MatchFound, ""), new LauncherSettings()));
    }

    [Fact]
    public void Settings_survive_save_and_load_and_old_files_get_defaults()
    {
        using var d = new Tmp();
        var store = new ProfileStore(Path.Combine(d.Path, ProfileStore.FileName));
        File.WriteAllText(store.FilePath, "{\"Version\":1,\"Profiles\":[{\"Name\":\"A\"}],\"Settings\":null}");
        var f = store.Load();
        Assert.NotNull(f.Settings);
        Assert.True(f.Profiles[0].IncludeInStartEverything);
        f.Settings.ClientStaggerSeconds = 9999;
        f.Settings.RichPresenceClientId = " 123 ";
        store.Save(f);
        var g = store.Load();
        Assert.Equal(300, g.Settings.ClientStaggerSeconds);
        Assert.Equal("123", g.Settings.RichPresenceClientId);
    }
}

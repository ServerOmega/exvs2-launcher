using System.Text.Json.Nodes;
using Exvs2Launcher.Core.Health;

namespace Exvs2Launcher.Core.Presence;

public enum PlayState { NotPlaying, Menus, Queue, Match }

/// <summary>
/// Best-effort "what is this PC doing" for Rich Presence. The launcher cannot read the game's screen, so it uses
/// what it can see: a client running = in menus; a queue line in the card server log = in queue (for a few minutes);
/// a MATCH line = in match until a result / room end / drop is logged (or a time limit passes).
/// </summary>
public sealed class PresenceTracker
{
    public static readonly TimeSpan QueueHold = TimeSpan.FromMinutes(3);
    public static readonly TimeSpan MatchHold = TimeSpan.FromMinutes(12);

    private DateTime? _queueAt, _matchAt;

    public void Observe(HealthEvent e)
    {
        switch (e.Kind)
        {
            case HealthEventKind.QueueActivity:
                _queueAt = e.At;
                break;
            case HealthEventKind.MatchFound:
                _matchAt = e.At;
                _queueAt = null;
                break;
            case HealthEventKind.ResultSaved:
            case HealthEventKind.RoomEnded:
            case HealthEventKind.PossibleDisconnect:
            case HealthEventKind.FailedBattle:
                _matchAt = null;
                break;
        }
    }

    public PlayState State(bool anyClientRunning, DateTime now)
    {
        if (!anyClientRunning) { _queueAt = _matchAt = null; return PlayState.NotPlaying; }
        if (_matchAt is DateTime m && now - m < MatchHold) return PlayState.Match;
        if (_queueAt is DateTime q && now - q < QueueHold) return PlayState.Queue;
        return PlayState.Menus;
    }

    public const string InviteUrl = "https://discord.gg/YENXAmjkYX";

    /// <summary>The Discord activity object for a state (null = clear presence).</summary>
    public static JsonObject? Activity(PlayState state, int cabsRunning, DateTimeOffset? since, bool inviteButton)
    {
        if (state == PlayState.NotPlaying) return null;
        var a = new JsonObject
        {
            ["details"] = "EXVS2 Infinite Boost",
            ["state"] = state switch
            {
                PlayState.Queue => "In queue",
                PlayState.Match => "In match",
                _ => "In menus",
            } + (cabsRunning > 1 ? $" · {cabsRunning} cabs" : ""),
        };
        if (since is DateTimeOffset s) a["timestamps"] = new JsonObject { ["start"] = s.ToUnixTimeSeconds() };
        if (inviteButton)
            a["buttons"] = new JsonArray(new JsonObject { ["label"] = "Join the EXVS2 Discord", ["url"] = InviteUrl });
        return a;
    }
}

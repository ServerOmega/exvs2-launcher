using Exvs2Launcher.Core.Health;
using Exvs2Launcher.Core.Matchmaking;
using Exvs2Launcher.Core.Profiles;

namespace Exvs2Launcher.Core.Notifications;

public sealed record Notice(string Kind, string Title, string Text);

/// <summary>
/// Decides when to notify about queue / match activity. Nothing fires on the first observation of a server (so
/// opening the launcher is silent), and the same kind of notice is held back for <see cref="MinGap"/>.
/// </summary>
public sealed class NotificationRules
{
    public static readonly TimeSpan MinGap = TimeSpan.FromSeconds(45);
    private readonly Func<DateTime> _now;
    private readonly Dictionary<string, DateTime> _last = new();

    public NotificationRules(Func<DateTime>? now = null) => _now = now ?? (() => DateTime.Now);

    public static bool AnyOutput(LauncherSettings s) => s.NotifySound || s.NotifyToast;

    public IReadOnlyList<Notice> FromStatus(string serverName, MatchmakerStatusInfo? previous, MatchmakerStatusInfo? current, LauncherSettings s)
    {
        var list = new List<Notice>();
        if (!AnyOutput(s) || previous is null || current is null) return list;
        if (s.NotifyOnMatch && (current.Matches > previous.Matches || current.InMatch > previous.InMatch))
            Add(list, new Notice("match", "New match", $"{serverName}: a match started ({current.Players})."));
        else if (s.NotifyOnQueueActivity && current.Searching > previous.Searching)
            Add(list, new Notice("queue", "Someone is searching",
                $"{serverName}: {current.Searching} player{(current.Searching == 1 ? "" : "s")} searching for a match."));
        return list;
    }

    public Notice? FromHealth(HealthEvent e, LauncherSettings s)
    {
        if (!AnyOutput(s) || !s.NotifyOnMatch || e.Kind != HealthEventKind.MatchFound) return null;
        var list = new List<Notice>();
        Add(list, new Notice("local-match", "Match found", $"{e.Cab}: match found."));
        return list.FirstOrDefault();
    }

    private void Add(List<Notice> list, Notice n)
    {
        var now = _now();
        if (_last.TryGetValue(n.Kind, out var t) && now - t < MinGap) return;
        _last[n.Kind] = now;
        list.Add(n);
    }
}

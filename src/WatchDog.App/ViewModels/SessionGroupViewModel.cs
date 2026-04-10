using WatchDog.Core.Sessions;

namespace WatchDog.App.ViewModels;

public sealed class SessionGroupViewModel
{
    public SessionGroupViewModel(GameSession session, IReadOnlyList<ClipItemViewModel> clips)
    {
        Session = session;
        SessionId = session.Id;
        GameName = session.GameName;
        StartedAt = session.StartedAt;
        EndedAt = session.EndedAt;
        Status = session.Status;
        MatchCount = session.Matches.Count;
        Clips = clips;
    }

    public GameSession Session { get; }
    public Guid SessionId { get; }
    public string GameName { get; }
    public DateTimeOffset StartedAt { get; }
    public DateTimeOffset? EndedAt { get; }
    public SessionStatus Status { get; }
    public int MatchCount { get; }
    public IReadOnlyList<ClipItemViewModel> Clips { get; }

    public int ClipCount => Clips.Count;

    public string DateDisplay => StartedAt.LocalDateTime.ToString("MMM dd, yyyy");

    public string TimeDisplay => StartedAt.LocalDateTime.ToString("h:mm tt");

    public string DurationDisplay
    {
        get
        {
            if (EndedAt is null) return "In progress";
            var duration = EndedAt.Value - StartedAt;
            return duration.TotalHours >= 1
                ? $"{(int)duration.TotalHours}h {duration.Minutes}m"
                : $"{(int)duration.TotalMinutes}m";
        }
    }

    public string StatusDisplay => Status switch
    {
        SessionStatus.InProgress => "LIVE",
        SessionStatus.Crashed => "CRASHED",
        _ => "",
    };

    public string SummaryDisplay
    {
        get
        {
            var parts = new List<string>();
            if (ClipCount > 0) parts.Add($"{ClipCount} clip{(ClipCount != 1 ? "s" : "")}");
            if (MatchCount > 0) parts.Add($"{MatchCount} match{(MatchCount != 1 ? "es" : "")}");
            parts.Add(DurationDisplay);
            return string.Join(" · ", parts);
        }
    }
}

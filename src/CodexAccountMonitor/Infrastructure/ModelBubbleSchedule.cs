namespace CodexAccountMonitor.Infrastructure;

/// <summary>Bubble timing driven by a monotonic elapsed clock, independent of account refresh cadence.</summary>
public sealed class ModelBubbleSchedule
{
    public static readonly TimeSpan Interval = TimeSpan.FromMinutes(5);
    public static readonly TimeSpan DisplayDuration = TimeSpan.FromSeconds(7);

    public bool IsEnabled { get; private set; }
    public bool IsVisible { get; private set; }
    public TimeSpan? NextShowAt { get; private set; }
    public TimeSpan? HideAt { get; private set; }

    /// <summary>Showing the model displays a bubble now and starts a new five-minute cycle.</summary>
    public void Show(TimeSpan now)
    {
        IsEnabled = true;
        IsVisible = true;
        HideAt = now + DisplayDuration;
        NextShowAt = now + Interval;
    }

    /// <summary>Hiding the model also cancels all future bubbles until it is shown again.</summary>
    public void Hide()
    {
        IsEnabled = false;
        IsVisible = false;
        HideAt = null;
        NextShowAt = null;
    }

    /// <returns>True only when a new bubble becomes due on this update.</returns>
    public bool Advance(TimeSpan now)
    {
        if (!IsEnabled) return false;
        if (NextShowAt is { } next && now >= next)
        {
            // Resume after suspension with one bubble, never a burst of missed intervals.
            Show(now);
            return true;
        }
        if (HideAt is { } hide && now >= hide)
        {
            IsVisible = false;
            HideAt = null;
        }
        return false;
    }
}

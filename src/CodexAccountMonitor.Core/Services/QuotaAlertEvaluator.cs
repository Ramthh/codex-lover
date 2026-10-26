using CodexAccountMonitor.Core.Models;

namespace CodexAccountMonitor.Core.Services;

public sealed record QuotaAlert(string Title, string Message);

/// <summary>Evaluates transitions from fresh service observations; timestamps alone never imply a reset.</summary>
public sealed class QuotaAlertEvaluator
{
    private readonly Dictionary<Guid, Observation> _observations = new();

    public IReadOnlyList<QuotaAlert> Evaluate(
        Guid id, string alias, AccountUsageState usage, AppSettings settings, DateTimeOffset localNow)
    {
        _observations.TryGetValue(id, out var previous);
        var alerts = new List<QuotaAlert>();
        var fresh = usage.Authentication == AuthenticationHealth.Healthy
            && string.IsNullOrWhiteSpace(usage.SafeErrorCode)
            && usage.LastSuccessfulRefreshUtc is { } succeeded
            && localNow - succeeded <= TimeSpan.FromMinutes(Math.Max(15, settings.RefreshIntervalMinutes * 2));
        var remaining = fresh ? MinimumRemaining(usage.Snapshot) : previous?.Remaining;
        var expired = usage.Authentication == AuthenticationHealth.Expired;
        var isNewReading = fresh && usage.LastSuccessfulRefreshUtc != previous?.LastSuccess;
        var quiet = IsQuietHour(settings, localNow.Hour);
        if (settings.NotificationsEnabled && !quiet)
        {
            if (settings.NotifyAuthenticationExpired && expired && previous?.Expired != true)
                alerts.Add(new("Sign-in needed", $"{alias}: sign in again to resume usage updates."));
            if (isNewReading && remaining is int value)
            {
                if (settings.NotifyLowQuota && value <= settings.LowQuotaThresholdPercent
                    && (previous?.Remaining is not int old || old > settings.LowQuotaThresholdPercent))
                    alerts.Add(new("Quota running low", $"{alias}: {value}% remains in the most constrained reported window."));
                if (settings.NotifyQuotaRecovered && value > 0 && previous?.Remaining == 0)
                    alerts.Add(new("Quota available again", $"{alias}: fresh usage data confirms quota is available again."));
            }
        }
        // Observe even when muted, so unmuting or leaving quiet hours cannot replay old events.
        _observations[id] = new(remaining, expired, fresh ? usage.LastSuccessfulRefreshUtc : previous?.LastSuccess);
        return alerts;
    }

    public static bool IsQuietHour(AppSettings settings, int hour) => settings.QuietHoursEnabled
        && (settings.QuietHoursStartHour == settings.QuietHoursEndHour
            || (settings.QuietHoursStartHour < settings.QuietHoursEndHour
                ? hour >= settings.QuietHoursStartHour && hour < settings.QuietHoursEndHour
                : hour >= settings.QuietHoursStartHour || hour < settings.QuietHoursEndHour));

    private static int? MinimumRemaining(QuotaSnapshot? snapshot)
    {
        var windows = new[] { snapshot?.FiveHour?.RemainingPercent, snapshot?.Weekly?.RemainingPercent };
        return windows.Where(static value => value.HasValue).Select(static value => value!.Value)
            .Cast<int?>().Min();
    }

    private sealed record Observation(int? Remaining, bool Expired, DateTimeOffset? LastSuccess);
}

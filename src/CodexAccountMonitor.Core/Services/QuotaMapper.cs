using CodexAccountMonitor.Core.Models;

namespace CodexAccountMonitor.Core.Services;

public static class QuotaMapper
{
    public const int FiveHourMinutes = 5 * 60;
    public const int WeeklyMinutes = 7 * 24 * 60;
    public const int OneDayMinutes = 24 * 60;

    public static QuotaSnapshot Map(RawRateLimitsResponse source)
    {
        ArgumentNullException.ThrowIfNull(source);

        var windows = SelectAccountWideWindows(source);
        var mappedWindows = windows
            .Select(MapWindow)
            .Where(static window => window is not null)
            .Cast<QuotaWindowSnapshot>()
            .ToArray();

        // Keep the persisted property names for backward compatibility, but treat
        // them as the short-term and long-term display slots. Codex can now report
        // plan-specific durations such as 30 days instead of exactly seven days.
        var shortTerm = mappedWindows.FirstOrDefault(
                            static value => DurationMinutes(value) == FiveHourMinutes)
                        ?? mappedWindows
                            .Where(static value => DurationMinutes(value) < OneDayMinutes)
                            .OrderBy(static value => DurationMinutes(value))
                            .FirstOrDefault();
        var longTerm = mappedWindows.FirstOrDefault(
                           static value => DurationMinutes(value) == WeeklyMinutes)
                       ?? mappedWindows
                           .Where(static value => DurationMinutes(value) >= OneDayMinutes)
                           .OrderBy(static value => DurationMinutes(value))
                           .FirstOrDefault();

        return new QuotaSnapshot(
            shortTerm,
            longTerm,
            source.ResetCreditsAvailable is null ? null : Math.Max(0, source.ResetCreditsAvailable.Value),
            source.RateLimitReachedType
            ?? source.DefaultBucket?.RateLimitReachedType
            ?? source.Buckets
                .Select(static bucket => bucket.RateLimitReachedType)
                .FirstOrDefault(static value => !string.IsNullOrWhiteSpace(value)));
    }

    private static IReadOnlyList<RawQuotaWindow> SelectAccountWideWindows(RawRateLimitsResponse source)
    {
        // `rateLimits` is the App Server's backward-compatible account-wide view.
        // Keep it authoritative even when it is incomplete so a zero-used
        // model-specific meter cannot be presented as 100% account quota.
        if (source.DefaultBucket is { } defaultBucket)
        {
            var windows = Windows(defaultBucket).ToList();
            foreach (var matchingBucket in source.Buckets.Where(
                         bucket => SameMeter(defaultBucket, bucket)))
            {
                foreach (var window in Windows(matchingBucket))
                {
                    AddOrCompleteSameDuration(windows, window);
                }
            }

            return windows;
        }

        // Older App Servers may expose only the map. An exact `codex` meter is the
        // aggregate. Never combine windows from different IDs because named/model
        // meters can have independent usage values.
        var canonical = source.Buckets.FirstOrDefault(IsCanonical);
        if (canonical is not null && HasWindow(canonical))
        {
            return Windows(canonical).ToArray();
        }

        // A single named meter is still model-specific. Do not relabel its
        // allowance as account-wide simply because no other meter was returned.
        return Array.Empty<RawQuotaWindow>();
    }

    private static IEnumerable<RawQuotaWindow> Windows(RawRateLimitBucket bucket) =>
        new[] { bucket.Primary, bucket.Secondary }
            .Where(static window => window?.WindowDurationMins is > 0)
            .Cast<RawQuotaWindow>();

    private static void AddOrCompleteSameDuration(List<RawQuotaWindow> windows, RawQuotaWindow candidate)
    {
        var index = windows.FindIndex(window =>
            window.WindowDurationMins == candidate.WindowDurationMins);
        if (index < 0)
        {
            windows.Add(candidate);
            return;
        }

        var preferred = windows[index];
        windows[index] = new RawQuotaWindow(
            preferred.UsedPercent ?? candidate.UsedPercent,
            preferred.WindowDurationMins ?? candidate.WindowDurationMins,
            preferred.ResetsAtUnixSeconds ?? candidate.ResetsAtUnixSeconds);
    }

    private static bool HasWindow(RawRateLimitBucket bucket) =>
        bucket.Primary?.WindowDurationMins is > 0
        || bucket.Secondary?.WindowDurationMins is > 0;

    private static bool SameMeter(RawRateLimitBucket first, RawRateLimitBucket second) =>
        string.Equals(first.LimitId, second.LimitId, StringComparison.OrdinalIgnoreCase);

    private static bool IsCanonical(RawRateLimitBucket bucket) =>
        string.Equals(bucket.LimitId, "codex", StringComparison.OrdinalIgnoreCase);

    private static int DurationMinutes(QuotaWindowSnapshot window) =>
        (int)Math.Round(window.WindowDuration.TotalMinutes, MidpointRounding.AwayFromZero);

    private static QuotaWindowSnapshot? MapWindow(RawQuotaWindow? source)
    {
        if (source?.UsedPercent is null || source.WindowDurationMins is not > 0)
        {
            return null;
        }

        var used = (int)Math.Round(
            Math.Clamp(source.UsedPercent.Value, 0m, 100m),
            MidpointRounding.AwayFromZero);

        DateTimeOffset? resetsAt = null;
        if (source.ResetsAtUnixSeconds is > 0)
        {
            try
            {
                resetsAt = DateTimeOffset.FromUnixTimeSeconds(source.ResetsAtUnixSeconds.Value);
            }
            catch (ArgumentOutOfRangeException)
            {
                resetsAt = null;
            }
        }

        return new QuotaWindowSnapshot(
            used,
            100 - used,
            TimeSpan.FromMinutes(source.WindowDurationMins.Value),
            resetsAt);
    }
}

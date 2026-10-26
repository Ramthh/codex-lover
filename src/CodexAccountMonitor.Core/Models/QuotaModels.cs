namespace CodexAccountMonitor.Core.Models;

public sealed record RawQuotaWindow(
    decimal? UsedPercent,
    int? WindowDurationMins,
    long? ResetsAtUnixSeconds);

public sealed record RawRateLimitBucket(
    string LimitId,
    string? LimitName,
    RawQuotaWindow? Primary,
    RawQuotaWindow? Secondary,
    string? RateLimitReachedType = null);

public sealed record RawRateLimitsResponse(
    IReadOnlyList<RawRateLimitBucket> Buckets,
    int? ResetCreditsAvailable,
    string? RateLimitReachedType = null,
    RawRateLimitBucket? DefaultBucket = null);

public sealed record QuotaWindowSnapshot(
    int UsedPercent,
    int RemainingPercent,
    TimeSpan WindowDuration,
    DateTimeOffset? ResetsAt);

public sealed record QuotaSnapshot(
    QuotaWindowSnapshot? FiveHour,
    QuotaWindowSnapshot? Weekly,
    int? ResetCreditsAvailable,
    string? RateLimitReachedType);

public sealed record AccountUsageState
{
    public QuotaSnapshot? Snapshot { get; init; }
    public AuthenticationHealth Authentication { get; init; } = AuthenticationHealth.NotConnected;
    public DateTimeOffset? LastSuccessfulRefreshUtc { get; init; }
    public DateTimeOffset? LastAttemptUtc { get; init; }
    public string? SafeErrorCode { get; init; }
    public bool IsRateLimitedResponse { get; init; }
    public bool HasPendingResetCredit { get; init; }
}

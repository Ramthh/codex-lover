using CodexAccountMonitor.Core.Models;

namespace CodexAccountMonitor.Core.Services;

public static class UsageStateReducer
{
    public static AccountUsageState Success(
        AccountUsageState previous,
        QuotaSnapshot snapshot,
        DateTimeOffset nowUtc) => previous with
    {
        Snapshot = snapshot,
        Authentication = AuthenticationHealth.Healthy,
        LastSuccessfulRefreshUtc = nowUtc,
        LastAttemptUtc = nowUtc,
        SafeErrorCode = null,
        IsRateLimitedResponse = false
    };

    public static AccountUsageState Failure(
        AccountUsageState previous,
        string safeErrorCode,
        DateTimeOffset nowUtc,
        bool isAuthenticationFailure = false,
        bool isRateLimited = false) => previous with
    {
        // Keep the last known quota snapshot. In particular, HTTP 429 is not 0% remaining.
        Snapshot = previous.Snapshot,
        Authentication = isAuthenticationFailure
            ? AuthenticationHealth.Expired
            : previous.Authentication == AuthenticationHealth.NotConnected
                ? AuthenticationHealth.Error
                : previous.Authentication,
        LastAttemptUtc = nowUtc,
        SafeErrorCode = safeErrorCode,
        IsRateLimitedResponse = isRateLimited
    };
}

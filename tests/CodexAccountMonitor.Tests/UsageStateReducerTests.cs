using CodexAccountMonitor.Core.Models;
using CodexAccountMonitor.Core.Services;

namespace CodexAccountMonitor.Tests;

public sealed class UsageStateReducerTests
{
    private static readonly DateTimeOffset Earlier =
        new(2030, 1, 1, 10, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Now =
        new(2030, 1, 1, 10, 5, 0, TimeSpan.Zero);

    [Fact]
    public void Success_ReplacesSnapshotAndClearsFailureState()
    {
        var previous = new AccountUsageState
        {
            Authentication = AuthenticationHealth.Expired,
            LastSuccessfulRefreshUtc = Earlier,
            LastAttemptUtc = Earlier,
            SafeErrorCode = "authentication_required",
            IsRateLimitedResponse = true
        };
        var snapshot = Snapshot(used: 25);

        var result = UsageStateReducer.Success(previous, snapshot, Now);

        Assert.Same(snapshot, result.Snapshot);
        Assert.Equal(AuthenticationHealth.Healthy, result.Authentication);
        Assert.Equal(Now, result.LastSuccessfulRefreshUtc);
        Assert.Equal(Now, result.LastAttemptUtc);
        Assert.Null(result.SafeErrorCode);
        Assert.False(result.IsRateLimitedResponse);
    }

    [Fact]
    public void Failure_AuthenticationFailureMarksExpiredAndPreservesLastGoodData()
    {
        var snapshot = Snapshot(used: 38);
        var previous = new AccountUsageState
        {
            Snapshot = snapshot,
            Authentication = AuthenticationHealth.Healthy,
            LastSuccessfulRefreshUtc = Earlier
        };

        var result = UsageStateReducer.Failure(
            previous,
            "authentication_required",
            Now,
            isAuthenticationFailure: true);

        Assert.Same(snapshot, result.Snapshot);
        Assert.Equal(AuthenticationHealth.Expired, result.Authentication);
        Assert.Equal(Earlier, result.LastSuccessfulRefreshUtc);
        Assert.Equal(Now, result.LastAttemptUtc);
        Assert.Equal("authentication_required", result.SafeErrorCode);
        Assert.False(result.IsRateLimitedResponse);
    }

    [Fact]
    public void Failure_FirstNonAuthenticationErrorMovesNotConnectedToError()
    {
        var result = UsageStateReducer.Failure(
            new AccountUsageState(),
            "network_failure",
            Now);

        Assert.Equal(AuthenticationHealth.Error, result.Authentication);
    }

    [Fact]
    public void Failure_NonAuthenticationErrorDoesNotExpireHealthyIdentity()
    {
        var result = UsageStateReducer.Failure(
            new AccountUsageState { Authentication = AuthenticationHealth.Healthy },
            "network_failure",
            Now);

        Assert.Equal(AuthenticationHealth.Healthy, result.Authentication);
    }

    [Fact]
    public void Failure_Http429PreservesRemainingQuotaInsteadOfDisplayingZero()
    {
        var snapshot = Snapshot(used: 36);
        var previous = new AccountUsageState
        {
            Snapshot = snapshot,
            Authentication = AuthenticationHealth.Healthy,
            LastSuccessfulRefreshUtc = Earlier
        };

        var result = UsageStateReducer.Failure(
            previous,
            "rate_limited",
            Now,
            isRateLimited: true);

        Assert.Same(snapshot, result.Snapshot);
        Assert.Equal(64, result.Snapshot?.FiveHour?.RemainingPercent);
        Assert.True(result.IsRateLimitedResponse);
        Assert.Equal(AuthenticationHealth.Healthy, result.Authentication);
        Assert.Equal(Earlier, result.LastSuccessfulRefreshUtc);
        Assert.Equal(Now, result.LastAttemptUtc);
    }

    [Fact]
    public void Failure_Http429WithoutPriorSnapshotDoesNotInventZeroQuota()
    {
        var result = UsageStateReducer.Failure(
            new AccountUsageState { Authentication = AuthenticationHealth.Healthy },
            "rate_limited",
            Now,
            isRateLimited: true);

        Assert.Null(result.Snapshot);
        Assert.True(result.IsRateLimitedResponse);
    }

    private static QuotaSnapshot Snapshot(int used) => new(
        new QuotaWindowSnapshot(
            used,
            100 - used,
            TimeSpan.FromHours(5),
            Now + TimeSpan.FromHours(1)),
        null,
        2,
        null);
}

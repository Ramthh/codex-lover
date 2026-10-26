using CodexAccountMonitor.Core.Models;
using CodexAccountMonitor.Core.Services;

namespace CodexAccountMonitor.Tests;

public sealed class DataFreshnessEvaluatorTests
{
    private static readonly DateTimeOffset Now =
        new(2030, 4, 5, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Evaluate_NoSuccessfulRefreshIsNeverFresh()
    {
        var result = DataFreshnessEvaluator.Evaluate(
            new AccountUsageState { LastAttemptUtc = Now },
            Now,
            TimeSpan.FromMinutes(10));

        Assert.Equal(DataFreshness.Never, result);
    }

    [Fact]
    public void Evaluate_RefreshAtExactBoundaryIsFresh()
    {
        var state = new AccountUsageState
        {
            LastSuccessfulRefreshUtc = Now - TimeSpan.FromMinutes(10)
        };

        var result = DataFreshnessEvaluator.Evaluate(
            state,
            Now,
            TimeSpan.FromMinutes(10));

        Assert.Equal(DataFreshness.Fresh, result);
    }

    [Fact]
    public void Evaluate_RefreshBeyondBoundaryIsStale()
    {
        var state = new AccountUsageState
        {
            LastSuccessfulRefreshUtc = Now - TimeSpan.FromMinutes(10) - TimeSpan.FromTicks(1)
        };

        var result = DataFreshnessEvaluator.Evaluate(
            state,
            Now,
            TimeSpan.FromMinutes(10));

        Assert.Equal(DataFreshness.Stale, result);
    }

    [Fact]
    public void Evaluate_SlightClockSkewIntoFutureRemainsFresh()
    {
        var state = new AccountUsageState
        {
            LastSuccessfulRefreshUtc = Now + TimeSpan.FromMinutes(1)
        };

        var result = DataFreshnessEvaluator.Evaluate(
            state,
            Now,
            TimeSpan.FromMinutes(10));

        Assert.Equal(DataFreshness.Fresh, result);
    }
}

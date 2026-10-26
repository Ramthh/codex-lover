using CodexAccountMonitor.Infrastructure;
using CodexAccountMonitor.Infrastructure.AppServer;

namespace CodexAccountMonitor.Tests;

public sealed class UsageRefreshRecoveryTests
{
    [Theory]
    [InlineData(1, 45, true)]
    [InlineData(1, 50, true)]
    [InlineData(2, 90, false)]
    [InlineData(2, 45, false)]
    [InlineData(1, 60, false)]
    [InlineData(2, 2, true)]
    [InlineData(3, 3, false)]
    public void RealRequestTimeoutGetsOneRecoveryAttemptWithoutAnUnboundedRetryLoop(
        int failedAttempt, int elapsedSeconds, bool expected)
    {
        Assert.Equal(expected, AccountMonitorService.ShouldRetryUsageRead(
            new TaskCanceledException(), failedAttempt, TimeSpan.FromSeconds(elapsedSeconds)));
    }

    [Theory]
    [InlineData("authentication")]
    [InlineData("rate_limit")]
    [InlineData("permissions")]
    [InlineData("isolation")]
    public void LongerRecoveryWindowNeverReplaysFailuresThatNeedUserAction(string failure)
    {
        Exception exception = failure switch
        {
            "authentication" => new AppServerRpcException(401, true, false),
            "rate_limit" => new AppServerRpcException(429, false, true),
            "permissions" => new UnauthorizedAccessException(),
            _ => new InvalidDataException()
        };

        Assert.False(AccountMonitorService.ShouldRetryUsageRead(
            exception, 1, TimeSpan.FromSeconds(45)));
    }
}

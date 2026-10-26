using CodexAccountMonitor.Core.Models;
using CodexAccountMonitor.Infrastructure;
using CodexAccountMonitor.ViewModels;

namespace CodexAccountMonitor.Tests;

public sealed class ModelUsageSummaryTests
{
    [Fact]
    public void MembershipUnavailableDoesNotRevealCachedAccountOrUsage()
    {
        var result = ModelUsageSummary.From(Card(50, 20), membershipAvailable: false);
        Assert.Equal("Sol", result.Title);
        Assert.Contains("Sign in to Codex Lover", result.Message);
        Assert.DoesNotContain("Personal", result.Message);
        Assert.DoesNotContain("%", result.Message);
        Assert.True(result.IsWarning);
    }

    [Fact]
    public void NoSelectionDoesNotPretendTheFirstAccountIsActive()
    {
        var result = ModelUsageSummary.From(null, membershipAvailable: true);
        Assert.Equal("No active Codex account selected.", result.Message);
        Assert.DoesNotContain("%", result.Message);
    }

    [Fact]
    public void MissingWindowAndExhaustedWindowRemainDistinct()
    {
        var result = ModelUsageSummary.From(Card(0, null), true);
        Assert.Contains("5h left: 0%", result.Message);
        Assert.Contains("Long-term left: Not reported", result.Message);
        Assert.DoesNotContain("Long-term left: 0%", result.Message);
    }

    [Fact]
    public void LabelsFollowTheReportedQuotaDurations()
    {
        var result = ModelUsageSummary.From(Card(72, 44, shortDuration: TimeSpan.FromMinutes(15), longDuration: TimeSpan.FromDays(30)), true);
        Assert.Contains("15m left: 72%", result.Message);
        Assert.Contains("30d left: 44%", result.Message);
        Assert.DoesNotContain("Weekly", result.Message);
    }

    [Fact]
    public void NotLoadedUsageIsUnavailableAndNeverZero()
    {
        var result = ModelUsageSummary.From(Card(null, null, refreshed: false), true);
        Assert.Contains("not available yet", result.Message);
        Assert.DoesNotContain("0%", result.Message);
        Assert.True(result.IsWarning);
    }

    [Theory]
    [InlineData(AuthenticationHealth.Expired, null)]
    [InlineData(AuthenticationHealth.Healthy, "authentication_required")]
    public void ExpiredAuthenticationLabelsCachedQuota(AuthenticationHealth authentication, string? error)
    {
        var result = ModelUsageSummary.From(Card(67, 42, authentication: authentication, error: error), true);
        Assert.Contains("Sign in again", result.Message);
        Assert.Contains("Last known", result.Message);
        Assert.Contains("67%", result.Message);
        Assert.True(result.IsWarning);
    }

    [Fact]
    public void RefreshErrorAndStalenessAreNotPresentedAsLiveUsage()
    {
        var failed = ModelUsageSummary.From(Card(51, 12, error: "rpc_500"), true);
        var stale = ModelUsageSummary.From(Card(51, 12, age: TimeSpan.FromHours(1)), true);
        Assert.Contains("refresh failed", failed.Message);
        Assert.Contains("Last known", failed.Message);
        Assert.Contains("out of date", stale.Message);
        Assert.Contains("Last known", stale.Message);
        Assert.True(stale.IsWarning);
    }

    [Fact]
    public void DemoIsClearlyMarkedAndAliasCannotOverrunTheTitle()
    {
        var result = ModelUsageSummary.From(Card(30, 60, alias: "Personal\n" + new string('x', 90)), false, isDemo: true);
        Assert.EndsWith(" · Preview", result.Title);
        Assert.True(result.Title.Length <= 46);
        Assert.DoesNotContain("\n", result.Title);
        Assert.Contains("Sample usage", result.Message);
    }

    private static AccountCardViewModel Card(int? shortRemaining, int? longRemaining,
        TimeSpan? shortDuration = null, TimeSpan? longDuration = null,
        AuthenticationHealth authentication = AuthenticationHealth.Healthy, string? error = null,
        bool refreshed = true, TimeSpan? age = null, string alias = "Personal")
    {
        static Task NoAction(AccountCardViewModel _) => Task.CompletedTask;
        var card = new AccountCardViewModel(1, NoAction, NoAction, NoAction, NoAction, NoAction);
        card.Update(new PersistedAccount(new AccountProfile { Id = Guid.NewGuid(), Slot = 1, Alias = alias },
            new AccountUsageState
            {
                Authentication = authentication,
                SafeErrorCode = error,
                LastSuccessfulRefreshUtc = refreshed ? DateTimeOffset.UtcNow - (age ?? TimeSpan.Zero) : null,
                Snapshot = new QuotaSnapshot(
                    shortRemaining is int s ? new QuotaWindowSnapshot(100 - s, s, shortDuration ?? TimeSpan.FromHours(5), null) : null,
                    longRemaining is int l ? new QuotaWindowSnapshot(100 - l, l, longDuration ?? TimeSpan.FromDays(7), null) : null,
                    null, null)
            }));
        card.IsActive = true;
        return card;
    }
}

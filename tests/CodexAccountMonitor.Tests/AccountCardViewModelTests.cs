using CodexAccountMonitor.Core.Models;
using CodexAccountMonitor.Infrastructure;
using CodexAccountMonitor.ViewModels;

namespace CodexAccountMonitor.Tests;

public sealed class AccountCardViewModelTests
{
    [Theory]
    [InlineData("rpc_401")]
    [InlineData("rpc_403")]
    [InlineData("rpc_-32603")]
    public void AuthenticationFailureExplainsSignInRecoveryBeforeGenericUsageFailure(string errorCode)
    {
        var account = AccountWithLongTerm(TimeSpan.FromDays(7), remaining: 55);
        var card = CreateCard();
        card.Update(account with
        {
            Usage = account.Usage with { Authentication = AuthenticationHealth.Expired, SafeErrorCode = errorCode }
        });

        Assert.True(card.NeedsReauthentication);
        Assert.Contains("Sign in again", card.StatusMessage);
        Assert.DoesNotContain("did not return usage data", card.StatusMessage);
        Assert.Equal("55%", card.WeeklyDisplay);
    }

    [Fact]
    public void MissingDesktopSignInGivesReauthenticationActionAndSpecificExplanation()
    {
        var account = AccountWithLongTerm(TimeSpan.FromDays(7), remaining: 55);
        var card = CreateCard();
        card.Update(account with { Usage = account.Usage with { SafeErrorCode = "desktop_sign_in_unavailable" } });

        Assert.True(card.NeedsReauthentication);
        Assert.Contains("Codex's saved sign-in", card.StatusMessage);
        Assert.Contains("Sign in again", card.RefreshStatusTooltip);
        Assert.True(card.ReauthenticateCommand.CanExecute(null));
    }

    [Fact]
    public void SevenDayQuotaKeepsTheWeeklyLabel()
    {
        var card = CreateCard();

        card.Update(AccountWithLongTerm(TimeSpan.FromDays(7), remaining: 66));

        Assert.Equal("Weekly left", card.LongTermLabel);
        Assert.Equal("66%", card.WeeklyDisplay);
        Assert.Equal(66, card.WeeklyProgress);
        Assert.Equal(1d, card.WeeklyProgressOpacity);
    }

    [Fact]
    public void ThirtyDayQuotaUsesAnAccurateDynamicLabel()
    {
        var card = CreateCard();
        var changed = new List<string?>();
        card.PropertyChanged += (_, args) => changed.Add(args.PropertyName);

        card.Update(AccountWithLongTerm(TimeSpan.FromDays(30), remaining: 100));

        Assert.Equal("30d left", card.LongTermLabel);
        Assert.Equal("100%", card.WeeklyDisplay);
        Assert.Contains(nameof(AccountCardViewModel.LongTermLabel), changed);
        Assert.Contains(nameof(AccountCardViewModel.WeeklyDisplay), changed);
        Assert.Contains(nameof(AccountCardViewModel.WeeklyProgress), changed);
    }

    [Fact]
    public void UnreportedLongTermQuotaLooksUnknownRatherThanExhausted()
    {
        var card = CreateCard();
        card.Update(AccountWithLongTerm(duration: null, remaining: null));

        Assert.Equal("Long-term left", card.LongTermLabel);
        Assert.Equal("—", card.WeeklyDisplay);
        Assert.False(card.HasWeekly);
        Assert.Equal(0, card.WeeklyProgress);
        Assert.Equal(0.25d, card.WeeklyProgressOpacity);
    }

    [Fact]
    public void LatestRefreshFailureIsVisibleWithoutDiscardingCachedQuota()
    {
        var account = AccountWithLongTerm(TimeSpan.FromDays(7), remaining: 55);
        account = account with
        {
            Usage = account.Usage with { SafeErrorCode = "rpc_500" }
        };
        var card = CreateCard();

        card.Update(account);

        Assert.True(card.HasRefreshError);
        Assert.Contains("did not return usage data", card.RefreshStatusTooltip);
        Assert.Equal("55%", card.WeeklyDisplay);
    }

    [Fact]
    public void SlowerRefreshScheduleDoesNotProducePrematureStaleWarning()
    {
        var account = AccountWithLongTerm(TimeSpan.FromDays(7), remaining: 55);
        account = account with { Usage = account.Usage with { LastSuccessfulRefreshUtc = DateTimeOffset.UtcNow.AddMinutes(-25) } };
        var card = CreateCard();
        card.Update(account);
        Assert.True(card.IsStale);

        card.ApplyDisplaySettings(refreshIntervalMinutes: 30, comfortableDensity: false);

        Assert.False(card.IsStale);
        Assert.False(card.NeedsAttention);
        Assert.False(card.HasStatusMessage);
    }

    [Fact]
    public void FailedRefreshNeedsAttentionEvenWhenAuthenticationIsHealthy()
    {
        var account = AccountWithLongTerm(TimeSpan.FromDays(7), remaining: 55);
        account = account with { Usage = account.Usage with { SafeErrorCode = "codex_not_found" } };
        var card = CreateCard();

        card.Update(account);

        Assert.True(card.NeedsAttention);
        Assert.True(card.NeedsCodexSetup);
        Assert.Contains("Settings", card.StatusMessage);
        Assert.True(card.RefreshCommand.CanExecute(null));
        Assert.Equal("55%", card.WeeklyDisplay);
    }

    [Theory]
    [InlineData(-1, "Reset due · refresh")]
    [InlineData(0.5, "Resets in <1m")]
    [InlineData(35, "Resets in 35m")]
    [InlineData(125, "Resets in 2h 5m")]
    [InlineData(3000, "Resets in 2d 2h")]
    public void ResetCountdownHandlesUpcomingAndElapsedWindows(double minutes, string expected)
    {
        var now = new DateTimeOffset(2026, 9, 5, 12, 0, 0, TimeSpan.Zero);
        Assert.Equal(expected, AccountCardViewModel.FormatResetCountdown(now.AddMinutes(minutes), now));
        Assert.Equal("Reset not reported", AccountCardViewModel.FormatResetCountdown(null, now));
    }

    [Fact]
    public void ClockRefreshNotifiesCountdownAndHealthChanges()
    {
        var card = CreateCard();
        var changed = new List<string?>();
        card.PropertyChanged += (_, args) => changed.Add(args.PropertyName);

        card.RefreshRelativeTime();

        Assert.Contains(nameof(AccountCardViewModel.WeeklyCountdown), changed);
        Assert.Contains(nameof(AccountCardViewModel.NeedsAttention), changed);
        Assert.Contains(nameof(AccountCardViewModel.StatusMessage), changed);
    }

    [Fact]
    public void PendingResetCanBeCheckedEvenAfterTheReportedBankReachesZero()
    {
        var account = AccountWithLongTerm(TimeSpan.FromDays(7), remaining: 55);
        account = account with
        {
            Usage = account.Usage with
            {
                HasPendingResetCredit = true,
                Snapshot = account.Usage.Snapshot! with { ResetCreditsAvailable = 0 }
            }
        };
        var card = CreateCard();

        card.Update(account);

        Assert.True(card.NeedsAttention);
        Assert.True(card.CanUseResetCredit);
        Assert.True(card.UseResetCreditCommand.CanExecute(null));
        Assert.Equal("Check reset result", card.ResetActionLabel);
        Assert.Contains("unconfirmed", card.StatusMessage);
    }

    private static AccountCardViewModel CreateCard() => new(
        1,
        _ => Task.CompletedTask,
        _ => Task.CompletedTask,
        _ => Task.CompletedTask,
        _ => Task.CompletedTask,
        _ => Task.CompletedTask);

    private static PersistedAccount AccountWithLongTerm(TimeSpan? duration, int? remaining)
    {
        QuotaWindowSnapshot? longTerm = null;
        if (duration is not null && remaining is not null)
        {
            longTerm = new QuotaWindowSnapshot(
                100 - remaining.Value,
                remaining.Value,
                duration.Value,
                DateTimeOffset.UtcNow.AddDays(1));
        }

        return new PersistedAccount(
            new AccountProfile
            {
                Id = Guid.NewGuid(),
                Slot = 1,
                Alias = "Test account",
                IdentityFingerprint = "test-fingerprint",
                AddedAtUtc = DateTimeOffset.UtcNow
            },
            new AccountUsageState
            {
                Snapshot = new QuotaSnapshot(null, longTerm, null, null),
                Authentication = AuthenticationHealth.Healthy,
                LastSuccessfulRefreshUtc = DateTimeOffset.UtcNow,
                LastAttemptUtc = DateTimeOffset.UtcNow
            });
    }
}

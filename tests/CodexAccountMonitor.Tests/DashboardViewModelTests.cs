using System.Collections.Specialized;
using CodexAccountMonitor.Core.Models;
using CodexAccountMonitor.Infrastructure;
using CodexAccountMonitor.ViewModels;

namespace CodexAccountMonitor.Tests;

public sealed class DashboardViewModelTests
{
    [Fact]
    public void EmptyDashboardShowsExactlyOneAddCard()
    {
        using var dashboard = CreateDashboard();

        var card = Assert.Single(dashboard.AllAccounts);
        Assert.Equal(1, card.Slot);
        Assert.False(card.IsConfigured);
        Assert.True(card.AddCommand.CanExecute(null));
        Assert.Equal(0, dashboard.ConnectedCount);
    }

    [Fact]
    public void EachConnectedAccountAddsOnlyOneFollowingEmptyCard()
    {
        using var dashboard = CreateDashboard();

        dashboard.UpdateCards(new[] { Account(1) });
        Assert.Equal(new[] { 1, 2 }, dashboard.AllAccounts.Select(static card => card.Slot));
        Assert.True(dashboard.AllAccounts[0].IsConfigured);
        Assert.False(dashboard.AllAccounts[1].IsConfigured);

        dashboard.UpdateCards(new[] { Account(1), Account(2) });
        Assert.Equal(new[] { 1, 2, 3 }, dashboard.AllAccounts.Select(static card => card.Slot));
        Assert.Equal(2, dashboard.AllAccounts.Count(static card => card.IsConfigured));
        Assert.Single(dashboard.AllAccounts, static card => !card.IsConfigured);
    }

    [Fact]
    public void SparseAssignedSlotsArePreservedAndAddCardRemainsLast()
    {
        using var dashboard = CreateDashboard();

        dashboard.UpdateCards(new[] { Account(7), Account(2) });

        Assert.Equal(new[] { 2, 7, 1 }, dashboard.AllAccounts.Select(static card => card.Slot));
        Assert.Equal("Account 02", dashboard.AllAccounts[0].Alias);
        Assert.Equal("Account 07", dashboard.AllAccounts[1].Alias);
        Assert.False(dashboard.AllAccounts[2].IsConfigured);
    }

    [Fact]
    public void TenConnectedAccountsCanAddAnEleventhAccount()
    {
        using var dashboard = CreateDashboard();
        var accounts = Enumerable.Range(1, 10).Select(static slot => Account(slot)).ToArray();

        dashboard.UpdateCards(accounts);

        Assert.Equal(11, dashboard.AllAccounts.Count);
        Assert.Equal(10, dashboard.AllAccounts.Count(static card => card.IsConfigured));
        Assert.Equal(11, dashboard.AllAccounts.Single(static card => !card.IsConfigured).Slot);
    }

    [Fact]
    public void RemovingAnAccountShrinksBackToConnectedCardsPlusOneAddCard()
    {
        using var dashboard = CreateDashboard();

        dashboard.UpdateCards(new[] { Account(1), Account(2), Account(3) });
        Assert.Equal(4, dashboard.AllAccounts.Count);

        dashboard.UpdateCards(new[] { Account(1) });

        Assert.Equal(new[] { 1, 2 }, dashboard.AllAccounts.Select(static card => card.Slot));
        Assert.True(dashboard.AllAccounts[0].IsConfigured);
        Assert.False(dashboard.AllAccounts[1].IsConfigured);
        Assert.Single(dashboard.AllAccounts, static card => !card.IsConfigured);
    }

    [Fact]
    public void VisibleCollectionNotifiesWhenCardsAreAdded()
    {
        using var dashboard = CreateDashboard();
        var notifications = new List<NotifyCollectionChangedEventArgs>();
        dashboard.AllAccounts.CollectionChanged += (_, args) => notifications.Add(args);

        dashboard.UpdateCards(new[] { Account(1) });

        Assert.Contains(notifications, static args => args.Action == NotifyCollectionChangedAction.Add);
    }

    [Fact]
    public void RefreshSummaryReportsWhenEveryAccountUpdated()
    {
        var started = DateTimeOffset.UtcNow;
        var accounts = new[]
        {
            Account(1, SuccessfulUsage(started.AddSeconds(1), hasLongTerm: true)),
            Account(2, SuccessfulUsage(started.AddSeconds(2), hasLongTerm: true))
        };

        var summary = DashboardViewModel.BuildRefreshSummary(accounts, started);

        Assert.Equal("All 2 accounts refreshed", summary);
    }

    [Fact]
    public void RefreshSummaryReportsFailuresAndUnavailableLongTermQuota()
    {
        var started = DateTimeOffset.UtcNow;
        var accounts = new[]
        {
            Account(1, SuccessfulUsage(started.AddSeconds(1), hasLongTerm: true)),
            Account(2, SuccessfulUsage(started.AddSeconds(2), hasLongTerm: false)),
            Account(3, new AccountUsageState
            {
                LastAttemptUtc = started.AddSeconds(3),
                SafeErrorCode = "rpc_500",
                Authentication = AuthenticationHealth.Error
            })
        };

        var summary = DashboardViewModel.BuildRefreshSummary(accounts, started);

        Assert.Equal("Refreshed 2/3 · 1 failed · 1 has no reported long-term quota", summary);
    }

    [Fact]
    public void HeaderDistinguishesConfiguredAccountsFromHealthyAccounts()
    {
        using var dashboard = CreateDashboard();
        dashboard.UpdateCards(new[]
        {
            Account(1, SuccessfulUsage(DateTimeOffset.UtcNow, true)),
            Account(2, new AccountUsageState { Authentication = AuthenticationHealth.Expired, SafeErrorCode = "authentication_required" })
        });

        Assert.Equal("2 accounts", dashboard.ConnectedLabel);
        Assert.Equal(1, dashboard.AttentionCount);
        Assert.True(dashboard.HasAttention);
        Assert.Equal("1 need attention", dashboard.AttentionSummary);
        Assert.Contains("awaiting first update", dashboard.LastUpdatedText);
    }

    [Fact]
    public void LatestSuccessfulAccountCannotHideOlderUsageInHeader()
    {
        var accounts = new[]
        {
            Account(1, SuccessfulUsage(DateTimeOffset.UtcNow.AddMinutes(-45), true)),
            Account(2, SuccessfulUsage(DateTimeOffset.UtcNow, true))
        };

        Assert.Equal("Oldest update 45m ago", DashboardViewModel.BuildFreshnessSummary(accounts));
    }

    [Fact]
    public void WarningCanBeExplicitlyDismissedAndIsNotStyledAsSuccess()
    {
        using var dashboard = CreateDashboard();
        dashboard.ShowToast("Refresh needs attention", severity: ToastSeverity.Warning);

        Assert.True(dashboard.HasToast);
        Assert.True(dashboard.ToastIsWarning);
        Assert.False(dashboard.ToastIsSuccess);
        dashboard.DismissToastCommand.Execute(null);
        Assert.False(dashboard.HasToast);
    }

    private static DashboardViewModel CreateDashboard() => new(
        monitor: null,
        settingsRepository: null,
        switchCoordinator: null,
        interaction: new StubInteractionService());

    private static PersistedAccount Account(int slot, AccountUsageState? usage = null) => new(
        new AccountProfile
        {
            Id = Guid.NewGuid(),
            Slot = slot,
            Alias = $"Account {slot:00}",
            IdentityFingerprint = $"fingerprint-{slot}",
            AddedAtUtc = DateTimeOffset.UtcNow
        },
        usage ?? new AccountUsageState());

    private static AccountUsageState SuccessfulUsage(DateTimeOffset attemptedUtc, bool hasLongTerm) => new()
    {
        Snapshot = new QuotaSnapshot(
            null,
            hasLongTerm
                ? new QuotaWindowSnapshot(25, 75, TimeSpan.FromDays(7), attemptedUtc.AddDays(1))
                : null,
            null,
            null),
        Authentication = AuthenticationHealth.Healthy,
        LastSuccessfulRefreshUtc = attemptedUtc,
        LastAttemptUtc = attemptedUtc
    };

    private sealed class StubInteractionService : IUserInteractionService
    {
        public Task<bool> EnrollAccountAsync(int slot) => Task.FromResult(false);
        public Task<bool> ReauthenticateAsync(Guid profileId) => Task.FromResult(false);
        public Task<bool> ConfirmRemoveAsync(string alias) => Task.FromResult(false);
        public Task<bool> ConfirmResetCreditAsync(string alias) => Task.FromResult(false);
        public void ShowSettings() { }
        public void ShowError(string title, string message) { }
    }
}

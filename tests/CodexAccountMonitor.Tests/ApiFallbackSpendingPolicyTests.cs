using CodexAccountMonitor.Core.Models;
using CodexAccountMonitor.Core.Services;

namespace CodexAccountMonitor.Tests;

public sealed class ApiFallbackSpendingPolicyTests
{
    [Fact]
    public void CanStart_DefaultDisabledSettingsRejectBilling()
    {
        var policy = new ApiFallbackSpendingPolicy(new ApiFallbackSettings());

        Assert.False(policy.CanStart(0m));
    }

    [Fact]
    public void CanStart_RequiresStoredCredential()
    {
        var policy = Policy(enabled: true, credential: false, limit: 10m, spent: 0m);

        Assert.False(policy.CanStart(1m));
    }

    [Fact]
    public void CanStart_RequiresPositiveSpendingLimit()
    {
        var policy = Policy(enabled: true, credential: true, limit: 0m, spent: 0m);

        Assert.False(policy.CanStart(0m));
    }

    [Fact]
    public void CanStart_AllowsExactRemainingBudgetButRejectsAnyOverage()
    {
        var policy = Policy(enabled: true, credential: true, limit: 10m, spent: 3m);

        Assert.True(policy.CanStart(7m));
        Assert.False(policy.CanStart(7.01m));
    }

    [Fact]
    public void RecordCharge_AccumulatesAndUpdatesAvailableBudget()
    {
        var policy = Policy(enabled: true, credential: true, limit: 10m, spent: 3m);

        policy.RecordCharge(6.5m);

        Assert.Equal(9.5m, policy.Current.SpentUsd);
        Assert.True(policy.CanStart(0.5m));
        Assert.False(policy.CanStart(0.51m));
    }

    [Fact]
    public void RecordCharge_RejectsOverageWithoutChangingCurrentSpend()
    {
        var policy = Policy(enabled: true, credential: true, limit: 5m, spent: 4m);

        var exception = Assert.Throws<InvalidOperationException>(() => policy.RecordCharge(1.01m));

        Assert.Contains("exceed", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(4m, policy.Current.SpentUsd);
    }

    [Fact]
    public async Task RecordCharge_SerializesConcurrentChargesAtTheLimit()
    {
        var policy = Policy(enabled: true, credential: true, limit: 1m, spent: 0m);

        await Task.WhenAll(Enumerable.Range(0, 100).Select(_ =>
            Task.Run(() => policy.RecordCharge(0.01m))));

        Assert.Equal(1m, policy.Current.SpentUsd);
        Assert.False(policy.CanStart(0.01m));
    }

    [Fact]
    public void CanStart_RejectsNegativeEstimate()
    {
        var policy = Policy(enabled: true, credential: true, limit: 1m, spent: 0m);

        Assert.Throws<ArgumentOutOfRangeException>(() => policy.CanStart(-0.01m));
    }

    [Fact]
    public void RecordCharge_RejectsNegativeActualCharge()
    {
        var policy = Policy(enabled: true, credential: true, limit: 1m, spent: 0m);

        Assert.Throws<ArgumentOutOfRangeException>(() => policy.RecordCharge(-0.01m));
    }

    [Fact]
    public void Constructor_RejectsNullSettings()
    {
        Assert.Throws<ArgumentNullException>(() => new ApiFallbackSpendingPolicy(null!));
    }

    [Theory]
    [InlineData(-1, 0)]
    [InlineData(1, -1)]
    public void Constructor_RejectsNegativeSpendingValues(int limit, int spent)
    {
        var settings = new ApiFallbackSettings
        {
            SpendingLimitUsd = limit,
            SpentUsd = spent
        };

        Assert.Throws<ArgumentOutOfRangeException>(() => new ApiFallbackSpendingPolicy(settings));
    }

    [Fact]
    public void Constructor_RejectsEnabledSettingsAlreadyOverLimit()
    {
        var settings = new ApiFallbackSettings
        {
            Enabled = true,
            HasStoredCredential = true,
            SpendingLimitUsd = 4m,
            SpentUsd = 5m
        };

        Assert.Throws<ArgumentException>(() => new ApiFallbackSpendingPolicy(settings));
    }

    private static ApiFallbackSpendingPolicy Policy(
        bool enabled,
        bool credential,
        decimal limit,
        decimal spent) => new(new ApiFallbackSettings
    {
        Enabled = enabled,
        HasStoredCredential = credential,
        SpendingLimitUsd = limit,
        SpentUsd = spent
    });
}

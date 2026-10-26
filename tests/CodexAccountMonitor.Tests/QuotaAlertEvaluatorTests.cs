using CodexAccountMonitor.Core.Models;
using CodexAccountMonitor.Core.Services;

namespace CodexAccountMonitor.Tests;

public sealed class QuotaAlertEvaluatorTests
{
    private readonly DateTimeOffset _now = new(2026, 9, 5, 12, 0, 0, TimeSpan.Zero);
    private readonly Guid _id = Guid.NewGuid();

    [Fact]
    public void LowQuotaAlertsOnceUntilQuotaRecoversAboveThreshold()
    {
        var evaluator = new QuotaAlertEvaluator();
        var settings = new AppSettings();
        Assert.Single(evaluator.Evaluate(_id, "Work", Usage(10, _now), settings, _now));
        Assert.Empty(evaluator.Evaluate(_id, "Work", Usage(9, _now.AddMinutes(1)), settings, _now.AddMinutes(1)));
        Assert.Empty(evaluator.Evaluate(_id, "Work", Usage(90, _now.AddMinutes(2)), settings, _now.AddMinutes(2)));
        Assert.Single(evaluator.Evaluate(_id, "Work", Usage(10, _now.AddMinutes(3)), settings, _now.AddMinutes(3)));
    }

    [Fact]
    public void ResetRequiresFreshPositiveUsageNotTheClock()
    {
        var evaluator = new QuotaAlertEvaluator();
        var settings = new AppSettings { NotifyLowQuota = false };
        evaluator.Evaluate(_id, "Work", Usage(0, _now), settings, _now);
        Assert.Empty(evaluator.Evaluate(_id, "Work", Usage(0, _now), settings, _now.AddHours(5)));
        var failed = Usage(100, _now.AddHours(5)) with { SafeErrorCode = "network_error" };
        Assert.Empty(evaluator.Evaluate(_id, "Work", failed, settings, _now.AddHours(5)));
        Assert.Single(evaluator.Evaluate(_id, "Work", Usage(100, _now.AddHours(5)), settings, _now.AddHours(5)));
    }

    [Fact]
    public void QuietHoursSuppressWithoutReplayingOldEvents()
    {
        var evaluator = new QuotaAlertEvaluator();
        var settings = new AppSettings { QuietHoursEnabled = true, QuietHoursStartHour = 22, QuietHoursEndHour = 8 };
        Assert.True(QuotaAlertEvaluator.IsQuietHour(settings, 23));
        Assert.True(QuotaAlertEvaluator.IsQuietHour(settings, 7));
        Assert.False(QuotaAlertEvaluator.IsQuietHour(settings, 8));
        var late = _now.AddHours(11);
        Assert.Empty(evaluator.Evaluate(_id, "Work", Usage(10, late), settings, late));
        Assert.Empty(evaluator.Evaluate(_id, "Work", Usage(9, late.AddHours(10)), settings, late.AddHours(10)));
    }

    [Fact]
    public void ExpirationAlertsOnceAndNullQuotaDoesNotImplyExhaustion()
    {
        var evaluator = new QuotaAlertEvaluator();
        var usage = new AccountUsageState { Authentication = AuthenticationHealth.Expired };
        Assert.Single(evaluator.Evaluate(_id, "Work", usage, new AppSettings(), _now));
        Assert.Empty(evaluator.Evaluate(_id, "Work", usage, new AppSettings(), _now));
        Assert.Empty(evaluator.Evaluate(Guid.NewGuid(), "Other", new AccountUsageState
        { Authentication = AuthenticationHealth.Healthy, LastSuccessfulRefreshUtc = _now }, new AppSettings(), _now));
    }

    private static AccountUsageState Usage(int remaining, DateTimeOffset time) => new()
    {
        Authentication = AuthenticationHealth.Healthy,
        LastSuccessfulRefreshUtc = time,
        Snapshot = new(new(100 - remaining, remaining, TimeSpan.FromHours(5), time.AddHours(1)), null, null, null)
    };
}

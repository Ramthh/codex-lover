using CodexAccountMonitor.Core.Models;
using CodexAccountMonitor.Core.Services;

namespace CodexAccountMonitor.Tests;

public sealed class QuotaMapperTests
{
    [Fact]
    public void Map_MapsKnownWindowsRemainingPercentAndResetTimestamps()
    {
        var fiveHourReset = new DateTimeOffset(2030, 9, 2, 12, 20, 0, TimeSpan.Zero);
        var weeklyReset = new DateTimeOffset(2030, 9, 7, 9, 0, 0, TimeSpan.Zero);
        var source = Response(
            new RawRateLimitBucket(
                "codex",
                "Codex",
                Window(27.5m, QuotaMapper.FiveHourMinutes, fiveHourReset),
                Window(110m, QuotaMapper.WeeklyMinutes, weeklyReset)),
            resetCredits: 3);

        var result = QuotaMapper.Map(source);

        Assert.NotNull(result.FiveHour);
        Assert.Equal(28, result.FiveHour.UsedPercent);
        Assert.Equal(72, result.FiveHour.RemainingPercent);
        Assert.Equal(TimeSpan.FromHours(5), result.FiveHour.WindowDuration);
        Assert.Equal(fiveHourReset, result.FiveHour.ResetsAt);

        Assert.NotNull(result.Weekly);
        Assert.Equal(100, result.Weekly.UsedPercent);
        Assert.Equal(0, result.Weekly.RemainingPercent);
        Assert.Equal(TimeSpan.FromDays(7), result.Weekly.WindowDuration);
        Assert.Equal(weeklyReset, result.Weekly.ResetsAt);
        Assert.Equal(3, result.ResetCreditsAvailable);
    }

    [Fact]
    public void Map_ClampsNegativeUsageToZero()
    {
        var source = Response(new RawRateLimitBucket(
            "codex",
            null,
            new RawQuotaWindow(-4m, QuotaMapper.FiveHourMinutes, null),
            null));

        var result = QuotaMapper.Map(source);

        Assert.NotNull(result.FiveHour);
        Assert.Equal(0, result.FiveHour.UsedPercent);
        Assert.Equal(100, result.FiveHour.RemainingPercent);
    }

    [Fact]
    public void Map_PrefersCanonicalCodexBucketWithoutSplicingAcrossLimitIds()
    {
        var complete = new RawRateLimitBucket(
            "workspace",
            null,
            new RawQuotaWindow(41m, QuotaMapper.FiveHourMinutes, null),
            new RawQuotaWindow(62m, QuotaMapper.WeeklyMinutes, null));
        var preferredButPartial = new RawRateLimitBucket(
            "codex",
            null,
            new RawQuotaWindow(7m, QuotaMapper.FiveHourMinutes, null),
            null);

        var result = QuotaMapper.Map(Response(preferredButPartial, complete));

        Assert.Equal(7, result.FiveHour?.UsedPercent);
        Assert.Null(result.Weekly);
    }

    [Fact]
    public void Map_DoesNotUseModelSpecificZeroUsageForCanonicalWeeklyQuota()
    {
        var modelSpecific = new RawRateLimitBucket(
            "codex_bengalfox",
            "GPT-5.3-Codex-Spark",
            new RawQuotaWindow(0m, QuotaMapper.FiveHourMinutes, null),
            new RawQuotaWindow(0m, QuotaMapper.WeeklyMinutes, null));
        var canonical = new RawRateLimitBucket(
            "codex",
            null,
            new RawQuotaWindow(28m, QuotaMapper.WeeklyMinutes, null),
            null);

        var result = QuotaMapper.Map(Response(modelSpecific, canonical));

        Assert.Null(result.FiveHour);
        Assert.Equal(28, result.Weekly?.UsedPercent);
        Assert.Equal(72, result.Weekly?.RemainingPercent);
    }

    [Fact]
    public void Map_DoesNotRelabelSingleModelMeterWhenCanonicalHasNoWindows()
    {
        var canonicalWithoutWindows = new RawRateLimitBucket("codex", null, null, null);
        var modelSpecific = new RawRateLimitBucket(
            "model_specific",
            "Model-specific limit",
            new RawQuotaWindow(15m, QuotaMapper.FiveHourMinutes, null),
            new RawQuotaWindow(25m, QuotaMapper.WeeklyMinutes, null));

        var result = QuotaMapper.Map(Response(canonicalWithoutWindows, modelSpecific));

        Assert.Null(result.FiveHour);
        Assert.Null(result.Weekly);
    }

    [Fact]
    public void Map_DoesNotMixFiveHourAndWeeklyWindowsFromDifferentLimitIds()
    {
        var fiveHourOnly = new RawRateLimitBucket(
            "codex",
            null,
            new RawQuotaWindow(35m, QuotaMapper.FiveHourMinutes, null),
            null);
        var weeklyOnly = new RawRateLimitBucket(
            "other",
            null,
            new RawQuotaWindow(80m, QuotaMapper.WeeklyMinutes, null),
            null);

        var result = QuotaMapper.Map(Response(fiveHourOnly, weeklyOnly));

        Assert.Equal(35, result.FiveHour?.UsedPercent);
        Assert.Null(result.Weekly);
    }

    [Fact]
    public void Map_UsesServiceReportedShortTermDurationInsteadOfDiscardingIt()
    {
        var source = Response(new RawRateLimitBucket(
            "codex",
            null,
            new RawQuotaWindow(null, QuotaMapper.FiveHourMinutes, null),
            new RawQuotaWindow(44m, 60, null)));

        var result = QuotaMapper.Map(source);

        Assert.Equal(44, result.FiveHour?.UsedPercent);
        Assert.Equal(TimeSpan.FromHours(1), result.FiveHour?.WindowDuration);
        Assert.Null(result.Weekly);
    }

    [Fact]
    public void Map_MapsThirtyDayWindowAsTheReportedLongTermQuota()
    {
        var source = Response(new RawRateLimitBucket(
            "codex",
            null,
            new RawQuotaWindow(19m, 30 * QuotaMapper.OneDayMinutes, null),
            null));

        var result = QuotaMapper.Map(source);

        Assert.Null(result.FiveHour);
        Assert.Equal(19, result.Weekly?.UsedPercent);
        Assert.Equal(81, result.Weekly?.RemainingPercent);
        Assert.Equal(TimeSpan.FromDays(30), result.Weekly?.WindowDuration);
    }

    [Fact]
    public void Map_DefaultBucketWinsOverAmbiguousModelSpecificBuckets()
    {
        var defaultBucket = new RawRateLimitBucket(
            "codex",
            null,
            new RawQuotaWindow(28m, QuotaMapper.WeeklyMinutes, null),
            null);
        var source = new RawRateLimitsResponse(
            new[]
            {
                new RawRateLimitBucket(
                    "codex_first_model",
                    null,
                    new RawQuotaWindow(0m, QuotaMapper.FiveHourMinutes, null),
                    new RawQuotaWindow(0m, QuotaMapper.WeeklyMinutes, null)),
                new RawRateLimitBucket(
                    "codex_second_model",
                    null,
                    new RawQuotaWindow(45m, QuotaMapper.FiveHourMinutes, null),
                    new RawQuotaWindow(55m, QuotaMapper.WeeklyMinutes, null))
            },
            null,
            DefaultBucket: defaultBucket);

        var result = QuotaMapper.Map(source);

        Assert.Null(result.FiveHour);
        Assert.Equal(28, result.Weekly?.UsedPercent);
        Assert.Equal(72, result.Weekly?.RemainingPercent);
    }

    [Fact]
    public void Map_MergesComplementaryWindowsOnlyForTheSameDefaultMeter()
    {
        var defaultBucket = new RawRateLimitBucket(
            "codex",
            null,
            null,
            new RawQuotaWindow(34m, QuotaMapper.WeeklyMinutes, 1_900_000_000));
        var source = new RawRateLimitsResponse(
            new[]
            {
                new RawRateLimitBucket(
                    "codex",
                    null,
                    new RawQuotaWindow(12m, QuotaMapper.FiveHourMinutes, 1_800_000_000),
                    new RawQuotaWindow(0m, QuotaMapper.WeeklyMinutes, 1_800_100_000)),
                new RawRateLimitBucket(
                    "other",
                    null,
                    new RawQuotaWindow(99m, 60, null),
                    null)
            },
            null,
            DefaultBucket: defaultBucket);

        var result = QuotaMapper.Map(source);

        Assert.Equal(12, result.FiveHour?.UsedPercent);
        Assert.Equal(34, result.Weekly?.UsedPercent);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1_900_000_000), result.Weekly?.ResetsAt);
    }

    [Fact]
    public void Map_MissingWeeklyUsageDoesNotAppearAsOneHundredPercentRemaining()
    {
        var source = Response(new RawRateLimitBucket(
            "codex",
            null,
            new RawQuotaWindow(20m, QuotaMapper.FiveHourMinutes, null),
            new RawQuotaWindow(null, QuotaMapper.WeeklyMinutes, null)));

        var result = QuotaMapper.Map(source);

        Assert.Equal(80, result.FiveHour?.RemainingPercent);
        Assert.Null(result.Weekly);
    }

    [Fact]
    public void Map_MultipleNonCanonicalBucketsAreAmbiguousAndRemainUnreported()
    {
        var firstModel = new RawRateLimitBucket(
            "codex_first_model",
            "First model",
            new RawQuotaWindow(0m, QuotaMapper.FiveHourMinutes, null),
            new RawQuotaWindow(0m, QuotaMapper.WeeklyMinutes, null));
        var secondModel = new RawRateLimitBucket(
            "codex_second_model",
            "Second model",
            new RawQuotaWindow(45m, QuotaMapper.FiveHourMinutes, null),
            new RawQuotaWindow(55m, QuotaMapper.WeeklyMinutes, null));

        var result = QuotaMapper.Map(Response(firstModel, secondModel));

        Assert.Null(result.FiveHour);
        Assert.Null(result.Weekly);
    }

    [Fact]
    public void Map_EmptyResponseLeavesQuotaWindowsAndCreditsUnreported()
    {
        var result = QuotaMapper.Map(new RawRateLimitsResponse(
            Array.Empty<RawRateLimitBucket>(),
            null));

        Assert.Null(result.FiveHour);
        Assert.Null(result.Weekly);
        Assert.Null(result.ResetCreditsAvailable);
        Assert.Null(result.RateLimitReachedType);
    }

    [Fact]
    public void Map_ClampsNegativeResetCreditBalanceToZero()
    {
        var result = QuotaMapper.Map(new RawRateLimitsResponse(
            Array.Empty<RawRateLimitBucket>(),
            -2));

        Assert.Equal(0, result.ResetCreditsAvailable);
    }

    [Fact]
    public void Map_UsesResponseRateLimitReasonBeforeBucketReason()
    {
        var result = QuotaMapper.Map(new RawRateLimitsResponse(
            new[]
            {
                new RawRateLimitBucket("codex", null, null, null, "bucket_reason")
            },
            0,
            "response_reason"));

        Assert.Equal("response_reason", result.RateLimitReachedType);
    }

    [Fact]
    public void Map_FallsBackToFirstReportedBucketRateLimitReason()
    {
        var result = QuotaMapper.Map(new RawRateLimitsResponse(
            new[]
            {
                new RawRateLimitBucket("first", null, null, null),
                new RawRateLimitBucket("second", null, null, null, "weekly_limit")
            },
            0));

        Assert.Equal("weekly_limit", result.RateLimitReachedType);
    }

    [Fact]
    public void Map_InvalidUnixResetTimestampIsTreatedAsUnreported()
    {
        var source = Response(new RawRateLimitBucket(
            "codex",
            null,
            new RawQuotaWindow(10m, QuotaMapper.FiveHourMinutes, long.MaxValue),
            null));

        var result = QuotaMapper.Map(source);

        Assert.NotNull(result.FiveHour);
        Assert.Null(result.FiveHour.ResetsAt);
    }

    [Fact]
    public void Map_ZeroUnixResetTimestampIsTreatedAsUnreported()
    {
        var source = Response(new RawRateLimitBucket(
            "codex",
            null,
            new RawQuotaWindow(10m, QuotaMapper.FiveHourMinutes, 0),
            null));

        Assert.Null(QuotaMapper.Map(source).FiveHour?.ResetsAt);
    }

    [Fact]
    public void Map_NullResponseThrows()
    {
        Assert.Throws<ArgumentNullException>(() => QuotaMapper.Map(null!));
    }

    private static RawRateLimitsResponse Response(
        RawRateLimitBucket bucket,
        int? resetCredits = null) => new(new[] { bucket }, resetCredits);

    private static RawRateLimitsResponse Response(
        RawRateLimitBucket first,
        RawRateLimitBucket second) => new(new[] { first, second }, null);

    private static RawQuotaWindow Window(
        decimal usedPercent,
        int durationMinutes,
        DateTimeOffset resetsAt) => new(
            usedPercent,
            durationMinutes,
            resetsAt.ToUnixTimeSeconds());
}

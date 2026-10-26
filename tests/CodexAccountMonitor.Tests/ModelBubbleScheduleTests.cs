using CodexAccountMonitor.Infrastructure;

namespace CodexAccountMonitor.Tests;

public sealed class ModelBubbleScheduleTests
{
    [Fact]
    public void InitialBubbleLastsExactlySevenSeconds()
    {
        var schedule = new ModelBubbleSchedule();
        schedule.Show(TimeSpan.Zero);
        Assert.True(schedule.IsVisible);
        Assert.False(schedule.Advance(TimeSpan.FromSeconds(6.999)));
        Assert.True(schedule.IsVisible);
        Assert.False(schedule.Advance(TimeSpan.FromSeconds(7)));
        Assert.False(schedule.IsVisible);
        Assert.Equal(TimeSpan.FromMinutes(5), schedule.NextShowAt);
    }

    [Fact]
    public void PeriodicBubbleStartsAtFiveMinuteBoundary()
    {
        var schedule = new ModelBubbleSchedule();
        schedule.Show(TimeSpan.Zero);
        Assert.False(schedule.Advance(TimeSpan.FromSeconds(299.999)));
        Assert.False(schedule.IsVisible);
        Assert.True(schedule.Advance(TimeSpan.FromMinutes(5)));
        Assert.True(schedule.IsVisible);
        Assert.Equal(TimeSpan.FromSeconds(307), schedule.HideAt);
        Assert.Equal(TimeSpan.FromMinutes(10), schedule.NextShowAt);
        Assert.False(schedule.Advance(TimeSpan.FromMinutes(5)));
    }

    [Fact]
    public void HideCancelsPendingBubblesAndShowStartsANewCycle()
    {
        var schedule = new ModelBubbleSchedule();
        schedule.Show(TimeSpan.Zero);
        schedule.Hide();
        Assert.False(schedule.IsEnabled);
        Assert.False(schedule.IsVisible);
        Assert.Null(schedule.NextShowAt);
        Assert.False(schedule.Advance(TimeSpan.FromHours(2)));
        schedule.Show(TimeSpan.FromHours(2));
        Assert.True(schedule.IsVisible);
        Assert.Equal(TimeSpan.FromMinutes(125), schedule.NextShowAt);
    }

    [Fact]
    public void OverdueTickCreatesOneBubbleWithoutCatchUpBurst()
    {
        var schedule = new ModelBubbleSchedule();
        schedule.Show(TimeSpan.Zero);
        Assert.True(schedule.Advance(TimeSpan.FromMinutes(37)));
        Assert.Equal(TimeSpan.FromMinutes(42), schedule.NextShowAt);
        Assert.False(schedule.Advance(TimeSpan.FromMinutes(37)));
        Assert.False(schedule.Advance(TimeSpan.FromMinutes(37) + TimeSpan.FromSeconds(7)));
        Assert.False(schedule.IsVisible);
    }

    [Fact]
    public void ExplicitShowRestartsExpiryAndPeriodicDeadline()
    {
        var schedule = new ModelBubbleSchedule();
        schedule.Show(TimeSpan.Zero);
        schedule.Show(TimeSpan.FromSeconds(4));
        schedule.Advance(TimeSpan.FromSeconds(7));
        Assert.True(schedule.IsVisible);
        Assert.Equal(TimeSpan.FromSeconds(11), schedule.HideAt);
        Assert.Equal(TimeSpan.FromSeconds(304), schedule.NextShowAt);
    }
}

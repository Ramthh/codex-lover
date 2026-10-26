using System.Globalization;
using CodexAccountMonitor.Core.Services;

namespace CodexAccountMonitor.Tests;

public sealed class ResetTimeFormatterTests
{
    [Fact]
    public void Format_NullTimestampReturnsNotReported()
    {
        var result = ResetTimeFormatter.Format(
            null,
            TimeZoneInfo.Utc,
            CultureInfo.InvariantCulture);

        Assert.Equal("Not reported", result);
    }

    [Fact]
    public void Format_ConvertsUtcTimestampToRequestedTimeZone()
    {
        var zone = TimeZoneInfo.CreateCustomTimeZone(
            "Test UTC+03",
            TimeSpan.FromHours(3),
            "Test UTC+03",
            "Test UTC+03");
        var timestamp = new DateTimeOffset(2030, 9, 2, 12, 20, 0, TimeSpan.Zero);

        var result = ResetTimeFormatter.Format(timestamp, zone, CultureInfo.InvariantCulture);

        Assert.Equal("Sep 2, 3:20 PM", result);
    }

    [Fact]
    public void Format_HandlesDateRolloverInNegativeOffset()
    {
        var zone = TimeZoneInfo.CreateCustomTimeZone(
            "Test UTC-05",
            TimeSpan.FromHours(-5),
            "Test UTC-05",
            "Test UTC-05");
        var timestamp = new DateTimeOffset(2030, 1, 1, 2, 5, 0, TimeSpan.Zero);

        var result = ResetTimeFormatter.Format(timestamp, zone, CultureInfo.InvariantCulture);

        Assert.Equal("Dec 31, 9:05 PM", result);
    }
}

using System.Globalization;

namespace CodexAccountMonitor.Core.Services;

public static class ResetTimeFormatter
{
    public static string Format(DateTimeOffset? utcValue, TimeZoneInfo timeZone, CultureInfo? culture = null)
    {
        if (utcValue is null)
        {
            return "Not reported";
        }

        var local = TimeZoneInfo.ConvertTime(utcValue.Value, timeZone);
        return local.ToString("MMM d, h:mm tt", culture ?? CultureInfo.CurrentCulture);
    }
}

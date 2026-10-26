namespace CodexAccountMonitor.Core.Models;

public sealed record AppSettings
{
    public bool AlwaysOnTop { get; init; }
    public bool ClickThrough { get; init; }
    public bool StartWithWindows { get; init; }
    public bool MinimizeToTray { get; init; } = true;
    public int WidgetOpacityPercent { get; init; } = 90;
    public bool ComfortableDensity { get; init; }
    public bool OpaqueBackground { get; init; } = true;
    public double? WindowLeft { get; init; }
    public double? WindowTop { get; init; }
    public double? WindowWidth { get; init; }
    public bool ExpandedLayout { get; init; }
    public bool ModelVisible { get; init; } = true;
    public string ModelCharacter { get; init; } = "Sol";
    public double? ModelLeft { get; init; }
    public double? ModelTop { get; init; }
    public int RefreshIntervalMinutes { get; init; } = 5;
    public bool NotificationsEnabled { get; init; } = true;
    public bool NotifyLowQuota { get; init; } = true;
    public bool NotifyAuthenticationExpired { get; init; } = true;
    public bool NotifyQuotaRecovered { get; init; } = true;
    public int LowQuotaThresholdPercent { get; init; } = 20;
    public bool QuietHoursEnabled { get; init; }
    public int QuietHoursStartHour { get; init; } = 22;
    public int QuietHoursEndHour { get; init; } = 8;
    public string? CodexExecutablePath { get; init; }
    public Guid? ActiveProfileId { get; init; }
    public ApiFallbackSettings ApiFallback { get; init; } = new();
    public int WidgetExperienceVersion { get; init; }
}

public sealed record ApiFallbackSettings
{
    public bool Enabled { get; init; }
    public decimal SpendingLimitUsd { get; init; }
    public decimal SpentUsd { get; init; }
    public bool HasStoredCredential { get; init; }
}

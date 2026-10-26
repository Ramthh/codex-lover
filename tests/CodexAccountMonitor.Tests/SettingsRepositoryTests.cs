using CodexAccountMonitor.Core.Models;
using CodexAccountMonitor.Infrastructure;

namespace CodexAccountMonitor.Tests;

public sealed class SettingsRepositoryTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "CodexAccountMonitorTests", Guid.NewGuid().ToString("N"));
    private AppDataPaths Paths => new(_root);

    [Fact]
    public async Task HiddenModelAndNegativeMonitorPositionPersistWithoutChangingExistingSettings()
    {
        var repository = new SettingsRepository(Paths);
        await repository.SaveAsync(new AppSettings { RefreshIntervalMinutes = 7, CodexExecutablePath = "chosen.exe", StartWithWindows = true });
        await repository.UpdateAsync(value => value with { ModelVisible = false, ModelLeft = -1800, ModelTop = 210 });
        var loaded = await repository.LoadAsync();
        Assert.False(loaded.ModelVisible);
        Assert.Equal(-1800, loaded.ModelLeft);
        Assert.Equal(210, loaded.ModelTop);
        Assert.Equal(7, loaded.RefreshIntervalMinutes);
        Assert.Equal("chosen.exe", loaded.CodexExecutablePath);
        Assert.True(loaded.StartWithWindows);
    }

    [Fact]
    public async Task LegacyDocumentKeepsExistingPreferencesAndGetsNewDefaultsWithoutAWrite()
    {
        const string legacy = "{\"alwaysOnTop\":true,\"clickThrough\":true,\"startWithWindows\":true,\"minimizeToTray\":false,\"widgetOpacityPercent\":55,\"refreshIntervalMinutes\":7}";
        Paths.EnsureBaseDirectories();
        await File.WriteAllTextAsync(Paths.SettingsFile, legacy);

        var loaded = await new SettingsRepository(Paths).LoadAsync();

        Assert.True(loaded.AlwaysOnTop);
        Assert.True(loaded.ClickThrough);
        Assert.True(loaded.StartWithWindows);
        Assert.False(loaded.MinimizeToTray);
        Assert.Equal(55, loaded.WidgetOpacityPercent);
        Assert.Equal(7, loaded.RefreshIntervalMinutes);
        Assert.True(loaded.NotificationsEnabled);
        Assert.Equal(20, loaded.LowQuotaThresholdPercent);
        Assert.False(loaded.QuietHoursEnabled);
        Assert.Null(loaded.WindowLeft);
        Assert.Equal(legacy, await File.ReadAllTextAsync(Paths.SettingsFile));
    }

    [Fact]
    public async Task NotificationUpdatePreservesTheLatestWindowPositionAndActiveProfile()
    {
        var repository = new SettingsRepository(Paths);
        var profile = Guid.NewGuid();
        await repository.SaveAsync(new AppSettings { ActiveProfileId = profile });
        await repository.UpdateAsync(current => current with
        {
            WindowLeft = -1200, WindowTop = 180, WindowWidth = 780, ExpandedLayout = true
        });
        await repository.UpdateAsync(current => current with
        {
            LowQuotaThresholdPercent = 15, QuietHoursEnabled = true, QuietHoursStartHour = 23,
            QuietHoursEndHour = 7, ComfortableDensity = true, OpaqueBackground = true,
            NotifyAuthenticationExpired = false, NotifyQuotaRecovered = false
        });

        var loaded = await new SettingsRepository(Paths).LoadAsync();
        Assert.Equal(profile, loaded.ActiveProfileId);
        Assert.Equal(-1200, loaded.WindowLeft);
        Assert.Equal(180, loaded.WindowTop);
        Assert.Equal(780, loaded.WindowWidth);
        Assert.True(loaded.ExpandedLayout);
        Assert.Equal(15, loaded.LowQuotaThresholdPercent);
        Assert.True(loaded.QuietHoursEnabled);
        Assert.Equal(23, loaded.QuietHoursStartHour);
        Assert.Equal(7, loaded.QuietHoursEndHour);
        Assert.True(loaded.ComfortableDensity);
        Assert.True(loaded.OpaqueBackground);
        Assert.False(loaded.NotifyAuthenticationExpired);
        Assert.False(loaded.NotifyQuotaRecovered);
    }

    [Theory]
    [InlineData(0, 22, 8)]
    [InlineData(100, 22, 8)]
    [InlineData(20, -1, 8)]
    [InlineData(20, 22, 24)]
    [InlineData(20, 8, 8)]
    public async Task InvalidNotificationSettingsCannotOverwriteExistingSettings(int threshold, int start, int end)
    {
        var repository = new SettingsRepository(Paths);
        await repository.SaveAsync(new AppSettings { WidgetOpacityPercent = 65 });
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => repository.UpdateAsync(current => current with
        {
            LowQuotaThresholdPercent = threshold, QuietHoursEnabled = true,
            QuietHoursStartHour = start, QuietHoursEndHour = end
        }));
        Assert.Equal(65, (await repository.LoadAsync()).WidgetOpacityPercent);
    }

    [Fact]
    public async Task DamagedOptionalValuesNormalizeWithoutChangingTheDocument()
    {
        const string damaged = "{\"refreshIntervalMinutes\":0,\"widgetOpacityPercent\":110,\"lowQuotaThresholdPercent\":0,\"quietHoursEnabled\":true,\"quietHoursStartHour\":24,\"quietHoursEndHour\":23,\"windowWidth\":-1,\"apiFallback\":null}";
        Paths.EnsureBaseDirectories();
        await File.WriteAllTextAsync(Paths.SettingsFile, damaged);
        var loaded = await new SettingsRepository(Paths).LoadAsync();
        Assert.Equal(1, loaded.RefreshIntervalMinutes);
        Assert.Equal(100, loaded.WidgetOpacityPercent);
        Assert.Equal(1, loaded.LowQuotaThresholdPercent);
        Assert.False(loaded.QuietHoursEnabled);
        Assert.Null(loaded.WindowWidth);
        Assert.NotNull(loaded.ApiFallback);
        Assert.Equal(damaged, await File.ReadAllTextAsync(Paths.SettingsFile));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}

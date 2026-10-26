using System.Text.Json;
using CodexAccountMonitor.Core.Models;

namespace CodexAccountMonitor.Infrastructure;

public sealed class SettingsRepository
{
    private readonly AppDataPaths _paths;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly JsonSerializerOptions _options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    public SettingsRepository(AppDataPaths paths)
    {
        _paths = paths;
    }

    public async Task<AppSettings> LoadAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!File.Exists(_paths.SettingsFile))
            {
                return new AppSettings();
            }

            await using var stream = File.OpenRead(_paths.SettingsFile);
            return NormalizeLoaded(await JsonSerializer.DeserializeAsync<AppSettings>(stream, _options, cancellationToken)
                       .ConfigureAwait(false)
                   ?? new AppSettings());
        }
        catch (JsonException)
        {
            return new AppSettings();
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SaveAsync(AppSettings settings, CancellationToken cancellationToken = default)
    {
        Validate(settings);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await SaveCoreAsync(settings, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<AppSettings> UpdateAsync(
        Func<AppSettings, AppSettings> update,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(update);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            AppSettings current;
            if (!File.Exists(_paths.SettingsFile))
            {
                current = new AppSettings();
            }
            else
            {
                try
                {
                    await using var stream = File.OpenRead(_paths.SettingsFile);
                    current = NormalizeLoaded(await JsonSerializer.DeserializeAsync<AppSettings>(stream, _options, cancellationToken)
                                  .ConfigureAwait(false)
                              ?? new AppSettings());
                }
                catch (JsonException)
                {
                    current = new AppSettings();
                }
            }

            var updated = update(current) ?? throw new InvalidOperationException("Settings update returned no value.");
            Validate(updated);
            await SaveCoreAsync(updated, cancellationToken).ConfigureAwait(false);
            return updated;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task SaveCoreAsync(AppSettings settings, CancellationToken cancellationToken)
    {
        _paths.EnsureBaseDirectories();
        var temporaryFile = _paths.SettingsFile + ".tmp";
        await using (var stream = new FileStream(
                         temporaryFile,
                         FileMode.Create,
                         FileAccess.Write,
                         FileShare.None,
                         4096,
                         FileOptions.WriteThrough))
        {
            await JsonSerializer.SerializeAsync(stream, settings, _options, cancellationToken)
                .ConfigureAwait(false);
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        }

        File.Move(temporaryFile, _paths.SettingsFile, true);
    }

    private static void Validate(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (settings.RefreshIntervalMinutes is < 1 or > 60)
        {
            throw new ArgumentOutOfRangeException(nameof(settings), "Refresh interval must be 1-60 minutes.");
        }

        if (settings.WidgetOpacityPercent is < 35 or > 100
            || settings.LowQuotaThresholdPercent is < 1 or > 99
            || settings.QuietHoursStartHour is < 0 or > 23
            || settings.QuietHoursEndHour is < 0 or > 23
            || (settings.QuietHoursEnabled && settings.QuietHoursStartHour == settings.QuietHoursEndHour))
        {
            throw new ArgumentOutOfRangeException(nameof(settings), "Appearance or notification settings are outside the supported range.");
        }

        if (!IsValidCoordinate(settings.ModelLeft) || !IsValidCoordinate(settings.ModelTop)
            || !IsValidCoordinate(settings.WindowLeft) || !IsValidCoordinate(settings.WindowTop)
            || (settings.WindowWidth is double width && (!double.IsFinite(width) || width < 200 || width > 10000)))
        {
            throw new ArgumentOutOfRangeException(nameof(settings), "Saved window bounds are invalid.");
        }
    }

    // Older documents receive defaults for new fields without rewriting the user's settings on load.
    private static AppSettings NormalizeLoaded(AppSettings settings) => settings with
    {
        RefreshIntervalMinutes = Math.Clamp(settings.RefreshIntervalMinutes, 1, 60),
        WidgetOpacityPercent = Math.Clamp(settings.WidgetOpacityPercent, 35, 100),
        LowQuotaThresholdPercent = Math.Clamp(settings.LowQuotaThresholdPercent, 1, 99),
        QuietHoursStartHour = Math.Clamp(settings.QuietHoursStartHour, 0, 23),
        QuietHoursEndHour = Math.Clamp(settings.QuietHoursEndHour, 0, 23),
        QuietHoursEnabled = settings.QuietHoursEnabled
            && Math.Clamp(settings.QuietHoursStartHour, 0, 23) != Math.Clamp(settings.QuietHoursEndHour, 0, 23),
        WindowLeft = IsValidCoordinate(settings.WindowLeft) ? settings.WindowLeft : null,
        WindowTop = IsValidCoordinate(settings.WindowTop) ? settings.WindowTop : null,
        ModelLeft = IsValidCoordinate(settings.ModelLeft) ? settings.ModelLeft : null,
        ModelTop = IsValidCoordinate(settings.ModelTop) ? settings.ModelTop : null,
        ModelCharacter = DesktopModels.Name(DesktopModels.Parse(settings.ModelCharacter)),
        WindowWidth = settings.WindowWidth is double width && double.IsFinite(width) && width is >= 200 and <= 10000
            ? width : null,
        ApiFallback = settings.ApiFallback ?? new ApiFallbackSettings()
    };

    private static bool IsValidCoordinate(double? value) => value is null || (double.IsFinite(value.Value) && Math.Abs(value.Value) <= 100000);
}

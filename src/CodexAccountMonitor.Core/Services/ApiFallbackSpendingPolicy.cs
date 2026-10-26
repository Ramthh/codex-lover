using CodexAccountMonitor.Core.Models;

namespace CodexAccountMonitor.Core.Services;

public sealed class ApiFallbackSpendingPolicy
{
    private readonly object _gate = new();
    private ApiFallbackSettings _settings;

    public ApiFallbackSpendingPolicy(ApiFallbackSettings settings)
    {
        _settings = Validate(settings);
    }

    public ApiFallbackSettings Current
    {
        get
        {
            lock (_gate)
            {
                return _settings;
            }
        }
    }

    public bool CanStart(decimal estimatedCostUsd)
    {
        if (estimatedCostUsd < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(estimatedCostUsd));
        }

        lock (_gate)
        {
            return _settings.Enabled
                && _settings.HasStoredCredential
                && _settings.SpendingLimitUsd > 0
                && _settings.SpentUsd + estimatedCostUsd <= _settings.SpendingLimitUsd;
        }
    }

    public void RecordCharge(decimal actualCostUsd)
    {
        if (actualCostUsd < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(actualCostUsd));
        }

        lock (_gate)
        {
            if (_settings.SpentUsd + actualCostUsd > _settings.SpendingLimitUsd)
            {
                throw new InvalidOperationException("Recording this charge would exceed the configured API spending limit.");
            }

            _settings = _settings with { SpentUsd = _settings.SpentUsd + actualCostUsd };
        }
    }

    private static ApiFallbackSettings Validate(ApiFallbackSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (settings.SpendingLimitUsd < 0 || settings.SpentUsd < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(settings), "API spending values cannot be negative.");
        }

        if (settings.SpentUsd > settings.SpendingLimitUsd && settings.Enabled)
        {
            throw new ArgumentException("API spending cannot exceed the configured limit.", nameof(settings));
        }

        return settings;
    }
}

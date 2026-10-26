using System.Globalization;
using System.Windows.Input;
using CodexAccountMonitor.Core.Models;
using CodexAccountMonitor.Core.Services;
using CodexAccountMonitor.Infrastructure;

namespace CodexAccountMonitor.ViewModels;

public sealed class AccountCardViewModel : ObservableObject
{
    private PersistedAccount? _account;
    private bool _isActive;
    private bool _isPending;
    private bool _isSwitching;
    private int _refreshIntervalMinutes = 5;
    private bool _comfortableDensity;

    public AccountCardViewModel(
        int slot,
        Func<AccountCardViewModel, Task> switchAccount,
        Func<AccountCardViewModel, Task> addAccount,
        Func<AccountCardViewModel, Task> reauthenticate,
        Func<AccountCardViewModel, Task> removeAccount,
        Func<AccountCardViewModel, Task> useResetCredit,
        Func<AccountCardViewModel, Task>? refreshAccount = null,
        Func<AccountCardViewModel, Task>? deactivateAccount = null,
        Action? manageAccount = null)
    {
        Slot = slot;
        SwitchCommand = new AsyncRelayCommand(() => switchAccount(this), () => IsConfigured && !IsActive);
        AddCommand = new AsyncRelayCommand(() => addAccount(this), () => !IsConfigured);
        ReauthenticateCommand = new AsyncRelayCommand(() => reauthenticate(this), () => IsConfigured);
        RemoveCommand = new AsyncRelayCommand(() => removeAccount(this), () => IsConfigured);
        UseResetCreditCommand = new AsyncRelayCommand(
            () => useResetCredit(this),
            () => CanUseResetCredit);
        RefreshCommand = new AsyncRelayCommand(() => refreshAccount?.Invoke(this) ?? Task.CompletedTask, () => IsConfigured);
        DeactivateCommand = new AsyncRelayCommand(() => deactivateAccount?.Invoke(this) ?? Task.CompletedTask, () => IsConfigured && (IsActive || IsPending));
        ManageCommand = new RelayCommand(() => manageAccount?.Invoke());
    }

    public int Slot { get; }
    public Guid? ProfileId => _account?.Profile.Id;
    public string AccountNumber => Slot.ToString("00", CultureInfo.InvariantCulture);
    public bool IsConfigured => _account is not null;
    public string Alias => _account?.Profile.Alias ?? "Empty account slot";
    public string? MaskedEmail => _account?.Profile.ShowMaskedEmail == true ? _account.Profile.MaskedEmail : null;
    public bool HasMaskedEmail => !string.IsNullOrWhiteSpace(MaskedEmail);

    public AuthenticationHealth Authentication =>
        _account?.Usage.Authentication ?? AuthenticationHealth.NotConnected;

    public string AuthenticationLabel => Authentication switch
    {
        AuthenticationHealth.Healthy when _account?.Profile.IdentityFingerprint is null => "Auth OK · ID unverified",
        AuthenticationHealth.Healthy => "Auth OK",
        AuthenticationHealth.Expired => "Authentication expired",
        AuthenticationHealth.Error => "Connection error",
        _ => "Not connected"
    };

    public bool NeedsReauthentication => Authentication == AuthenticationHealth.Expired
        || _account?.Usage.SafeErrorCode is "authentication_required" or "identity_mismatch" or "desktop_sign_in_unavailable";
    public bool HasError => Authentication is AuthenticationHealth.Expired or AuthenticationHealth.Error;
    public bool NeedsAttention => IsConfigured && (HasError || HasRefreshError || IsStale || HasPendingResetCredit || _account?.Usage.LastSuccessfulRefreshUtc is null);
    public bool NeedsCodexSetup => _account?.Usage.SafeErrorCode is "codex_not_found" or "codex_untrusted";
    public string StatusMessage => !IsConfigured ? string.Empty
        : NeedsReauthentication ? _account?.Usage.SafeErrorCode is "authentication_required" or "identity_mismatch" or "desktop_sign_in_unavailable"
            ? DescribeRefreshError(_account.Usage.SafeErrorCode)
            : "Sign-in has expired. Sign in again to update usage."
        : HasRefreshError ? DescribeRefreshError(_account!.Usage.SafeErrorCode!)
        : HasPendingResetCredit ? "Reset result is unconfirmed. Select Check reset result to safely check the saved attempt."
        : IsStale ? "Update overdue. Showing the last known usage."
        : _account?.Usage.LastSuccessfulRefreshUtc is null ? "Usage has not been loaded yet."
        : string.Empty;
    public bool HasStatusMessage => !string.IsNullOrEmpty(StatusMessage);
    public string StaleTooltip => $"Usage is overdue after {StaleAfter.TotalMinutes:0} minutes without a successful update.";
    public TimeSpan StaleAfter => TimeSpan.FromMinutes(Math.Max(15, _refreshIntervalMinutes * 2));
    public double DetailFontSize => _comfortableDensity ? 13 : 11;
    public double MetricFontSize => _comfortableDensity ? 14 : 12;
    public double MetricRowHeight => _comfortableDensity ? 32 : 27;
    public System.Windows.Thickness CardPadding => _comfortableDensity ? new(14, 13, 14, 13) : new(12, 9, 12, 9);
    public bool IsRateLimited => _account?.Usage.IsRateLimitedResponse == true;

    public int? FiveHourRemaining => _account?.Usage.Snapshot?.FiveHour?.RemainingPercent;
    public int? WeeklyRemaining => _account?.Usage.Snapshot?.Weekly?.RemainingPercent;
    public bool HasFiveHour => FiveHourRemaining is not null;
    public bool HasWeekly => WeeklyRemaining is not null;
    public double FiveHourProgress => FiveHourRemaining ?? 0;
    public double WeeklyProgress => WeeklyRemaining ?? 0;
    public double FiveHourProgressOpacity => HasFiveHour ? 1d : 0.25d;
    public double WeeklyProgressOpacity => HasWeekly ? 1d : 0.25d;
    public string FiveHourDisplay => FiveHourRemaining is int value ? $"{value}%" : "—";
    public string WeeklyDisplay => WeeklyRemaining is int value ? $"{value}%" : "—";
    public string FiveHourLabel => FormatWindowLabel(
        _account?.Usage.Snapshot?.FiveHour?.WindowDuration,
        "5h left");
    public string LongTermLabel => FormatWindowLabel(
        _account?.Usage.Snapshot?.Weekly?.WindowDuration,
        "Long-term left");
    public string FiveHourReset => ResetTimeFormatter.Format(
        _account?.Usage.Snapshot?.FiveHour?.ResetsAt,
        TimeZoneInfo.Local);
    public string WeeklyReset => ResetTimeFormatter.Format(
        _account?.Usage.Snapshot?.Weekly?.ResetsAt,
        TimeZoneInfo.Local);
    public string FiveHourCountdown => FormatResetCountdown(_account?.Usage.Snapshot?.FiveHour?.ResetsAt, DateTimeOffset.UtcNow);
    public string WeeklyCountdown => FormatResetCountdown(_account?.Usage.Snapshot?.Weekly?.ResetsAt, DateTimeOffset.UtcNow);
    public string FiveHourResetTooltip => FormatUtcTooltip(_account?.Usage.Snapshot?.FiveHour?.ResetsAt);
    public string WeeklyResetTooltip => FormatUtcTooltip(_account?.Usage.Snapshot?.Weekly?.ResetsAt);
    public int? ResetCreditsAvailable => _account?.Usage.Snapshot?.ResetCreditsAvailable;
    public string ResetBankDisplay => ResetCreditsAvailable?.ToString(CultureInfo.InvariantCulture) ?? "—";
    public bool HasPendingResetCredit => _account?.Usage.HasPendingResetCredit == true;
    public string ResetActionLabel => HasPendingResetCredit ? "Check reset result" : "Use reset";
    public bool CanUseResetCredit => IsConfigured && (ResetCreditsAvailable is > 0 || HasPendingResetCredit) && Authentication == AuthenticationHealth.Healthy;
    public bool HasRefreshError => !string.IsNullOrWhiteSpace(_account?.Usage.SafeErrorCode);
    public string RefreshStatusTooltip
    {
        get
        {
            var status = $"{AuthenticationLabel}\n{LastRefreshTooltip}";
            return _account?.Usage.SafeErrorCode is { Length: > 0 } errorCode
                ? $"{status}\n{DescribeRefreshError(errorCode)}"
                : status;
        }
    }

    public string LastRefreshLabel
    {
        get
        {
            var refreshed = _account?.Usage.LastSuccessfulRefreshUtc;
            if (refreshed is null)
            {
                return "Never refreshed";
            }

            var elapsed = DateTimeOffset.UtcNow - refreshed.Value;
            if (elapsed < TimeSpan.FromMinutes(1))
            {
                return "just now";
            }

            if (elapsed < TimeSpan.FromHours(1))
            {
                return $"{Math.Max(1, (int)elapsed.TotalMinutes)}m ago";
            }

            return elapsed < TimeSpan.FromDays(1)
                ? $"{(int)elapsed.TotalHours}h ago"
                : refreshed.Value.ToLocalTime().ToString("MMM d", CultureInfo.CurrentCulture);
        }
    }

    public string LastRefreshTooltip => _account?.Usage.LastSuccessfulRefreshUtc?.ToLocalTime()
                                           .ToString("F", CultureInfo.CurrentCulture)
                                       ?? "No successful refresh yet";
    public bool IsStale => _account is not null
                           && DataFreshnessEvaluator.Evaluate(
                               _account.Usage,
                               DateTimeOffset.UtcNow,
                               StaleAfter) == DataFreshness.Stale;

    public bool IsActive
    {
        get => _isActive;
        set
        {
            if (SetProperty(ref _isActive, value))
            {
                RaiseStateCommands();
            }
        }
    }

    public bool IsPending
    {
        get => _isPending;
        set { if (SetProperty(ref _isPending, value)) RaiseStateCommands(); }
    }

    public bool IsSwitching
    {
        get => _isSwitching;
        set => SetProperty(ref _isSwitching, value);
    }

    public ICommand SwitchCommand { get; }
    public ICommand AddCommand { get; }
    public ICommand ReauthenticateCommand { get; }
    public ICommand RemoveCommand { get; }
    public ICommand UseResetCreditCommand { get; }
    public ICommand RefreshCommand { get; }
    public ICommand DeactivateCommand { get; }
    public ICommand ManageCommand { get; }

    public void ApplyDisplaySettings(int refreshIntervalMinutes, bool comfortableDensity)
    {
        _refreshIntervalMinutes = Math.Clamp(refreshIntervalMinutes, 1, 60);
        _comfortableDensity = comfortableDensity;
        foreach (var property in new[] { nameof(DetailFontSize), nameof(MetricFontSize), nameof(MetricRowHeight), nameof(CardPadding), nameof(StaleTooltip) })
            OnPropertyChanged(property);
        RefreshRelativeTime();
    }

    public void Update(PersistedAccount? account)
    {
        _account = account;
        foreach (var property in typeof(AccountCardViewModel).GetProperties())
        {
            if (property.Name is not nameof(Slot) and not nameof(AccountNumber))
            {
                OnPropertyChanged(property.Name);
            }
        }

        RaiseStateCommands();
    }

    public void RefreshRelativeTime()
    {
        OnPropertyChanged(nameof(LastRefreshLabel));
        OnPropertyChanged(nameof(IsStale));
        OnPropertyChanged(nameof(NeedsAttention));
        OnPropertyChanged(nameof(StatusMessage));
        OnPropertyChanged(nameof(HasStatusMessage));
        OnPropertyChanged(nameof(FiveHourCountdown));
        OnPropertyChanged(nameof(WeeklyCountdown));
    }

    private void RaiseStateCommands()
    {
        (SwitchCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        (AddCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        (ReauthenticateCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        (RemoveCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        (UseResetCreditCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        (RefreshCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        (DeactivateCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
    }

    internal static string FormatResetCountdown(DateTimeOffset? reset, DateTimeOffset now)
    {
        if (reset is null) return "Reset not reported";
        var remaining = reset.Value - now;
        if (remaining <= TimeSpan.Zero) return "Reset due · refresh";
        if (remaining.TotalMinutes < 1) return "Resets in <1m";
        if (remaining.TotalHours < 1) return $"Resets in {Math.Ceiling(remaining.TotalMinutes):0}m";
        if (remaining.TotalDays < 1) return $"Resets in {(int)remaining.TotalHours}h {remaining.Minutes}m";
        return $"Resets in {(int)remaining.TotalDays}d {remaining.Hours}h";
    }

    private static string FormatUtcTooltip(DateTimeOffset? value) => value is null
        ? "Reset time not reported"
        : $"{value.Value.ToLocalTime():F}\nUTC: {value.Value.UtcDateTime:u}";

    private static string FormatWindowLabel(TimeSpan? duration, string fallback)
    {
        if (duration is null || duration <= TimeSpan.Zero)
        {
            return fallback;
        }

        var minutes = (int)Math.Round(duration.Value.TotalMinutes, MidpointRounding.AwayFromZero);
        if (minutes == QuotaMapper.WeeklyMinutes)
        {
            return "Weekly left";
        }

        if (minutes == QuotaMapper.OneDayMinutes)
        {
            return "Daily left";
        }

        if (minutes % QuotaMapper.OneDayMinutes == 0)
        {
            return $"{minutes / QuotaMapper.OneDayMinutes}d left";
        }

        if (minutes % 60 == 0)
        {
            return $"{minutes / 60}h left";
        }

        return $"{minutes}m left";
    }

    private static string DescribeRefreshError(string errorCode) => errorCode switch
    {
        "authentication_required" => "Sign-in is required. Sign in again to update usage.",
        "identity_mismatch" => "This profile's account identity changed. Sign in with the original account.",
        "desktop_sign_in_unavailable" => "Codex's saved sign-in is no longer available for this account. Sign in again to reconnect it.",
        "codex_not_found" => "Codex could not be found. Open Settings to select its executable.",
        "codex_untrusted" => "Codex could not be verified. Check its installation in Settings.",
        "profile_isolation_failed" => "Codex reported a different account folder. Restart the monitor and retry to keep accounts separate.",
        "profile_access_denied" => "Windows could not access this account's local profile. Check its folder permissions and retry.",
        "rate_limited" => "Latest refresh was rate-limited; showing the last known values.",
        "connection_unavailable" => "Could not reconnect to Codex. Showing the last known values; automatic refresh will try again.",
        "rpc_401" or "rpc_403" => "Sign-in has expired. Sign in again to update usage.",
        _ when errorCode.StartsWith("rpc_", StringComparison.Ordinal) =>
            "Latest refresh failed because Codex did not return usage data.",
        _ => "Latest refresh failed; showing the last known values when available."
    };
}

using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows.Input;
using CodexAccountMonitor.Core.Models;
using CodexAccountMonitor.Infrastructure;

namespace CodexAccountMonitor.ViewModels;

public sealed class SettingsAccountRowViewModel : ObservableObject
{
    private string _editableAlias;

    public SettingsAccountRowViewModel(PersistedAccount account, Func<Task> reauthenticate, Func<Task> remove,
        Func<string, Task> rename, Func<int, Task> move)
    {
        ProfileId = account.Profile.Id;
        Slot = account.Profile.Slot;
        Alias = account.Profile.Alias;
        _editableAlias = Alias;
        MaskedEmail = account.Profile.MaskedEmail ?? "Email not reported";
        Health = account.Usage.Authentication switch
        {
            AuthenticationHealth.Healthy => "Connected",
            AuthenticationHealth.Expired => "Sign-in expired",
            AuthenticationHealth.Error => "Connection error",
            _ => "Not connected"
        };
        ReauthenticateCommand = new AsyncRelayCommand(reauthenticate);
        RemoveCommand = new AsyncRelayCommand(remove);
        SaveAliasCommand = new AsyncRelayCommand(() => rename(EditableAlias), () => EditableAlias.Trim() != Alias);
        MoveUpCommand = new AsyncRelayCommand(() => move(Slot - 1), () => Slot > 1);
        MoveDownCommand = new AsyncRelayCommand(() => move(Slot + 1), () => Slot < int.MaxValue);
    }

    public Guid ProfileId { get; }
    public int Slot { get; }
    public string SlotDisplay => Slot.ToString("00", CultureInfo.InvariantCulture);
    public string Alias { get; }
    public string EditableAlias
    {
        get => _editableAlias;
        set
        {
            if (SetProperty(ref _editableAlias, value ?? string.Empty))
                (SaveAliasCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        }
    }
    public string MaskedEmail { get; }
    public string Health { get; }
    public ICommand ReauthenticateCommand { get; }
    public ICommand RemoveCommand { get; }
    public ICommand SaveAliasCommand { get; }
    public ICommand MoveUpCommand { get; }
    public ICommand MoveDownCommand { get; }
}

public sealed record HourChoice(int Value, string Display);

public sealed class SettingsViewModel : ObservableObject
{
    private readonly SettingsRepository _repository;
    private readonly AccountMonitorService _monitor;
    private readonly CredentialManagerSecretStore _credentials;
    private readonly StartupRegistrationService _startup;
    private readonly IUserInteractionService _interaction;
    private readonly ICodexInstallationProbe _installationProbe;
    private readonly CancellationTokenSource _lifetime = new();
    private bool _initialStartupEnabled;
    private bool _alwaysOnTop;
    private bool _clickThrough;
    private bool _startWithWindows;
    private bool _minimizeToTray;
    private bool _comfortableDensity;
    private bool _opaqueBackground;
    private int _widgetOpacityPercent = 72;
    private int _refreshIntervalMinutes = 5;
    private bool _notificationsEnabled = true;
    private bool _notifyLowQuota = true;
    private bool _notifyAuthenticationExpired = true;
    private bool _notifyQuotaRecovered = true;
    private int _lowQuotaThresholdPercent = 20;
    private bool _quietHoursEnabled;
    private int _quietHoursStartHour = 22;
    private int _quietHoursEndHour = 8;
    private string? _codexExecutablePath;
    private string _codexExecutableStatus = "Checking the Codex installation…";
    private string _detectedCodexVersion = "Not checked";
    private string? _detectedCodexPath;
    private string _status = string.Empty;
    private bool _isSaving;
    private bool _isTestingConnection;
    private bool _isUpdatingAccounts;

    public SettingsViewModel(SettingsRepository repository, AccountMonitorService monitor,
        CredentialManagerSecretStore credentials, StartupRegistrationService startup,
        IUserInteractionService interaction, ICodexInstallationProbe? installationProbe = null)
    {
        _repository = repository;
        _monitor = monitor;
        _credentials = credentials;
        _startup = startup;
        _interaction = interaction;
        _installationProbe = installationProbe ?? new CodexInstallationProbe();
        AddAccountCommand = new AsyncRelayCommand(AddAccountAsync, () => CanEdit);
        TestConnectionCommand = new AsyncRelayCommand(TestConnectionAsync, () => !IsTestingConnection && CanEdit);
        CancelCommand = new RelayCommand(() => RequestClose?.Invoke(this, false), () => !IsSaving && !_isUpdatingAccounts);
    }

    public event EventHandler<bool>? RequestClose;
    public ObservableCollection<SettingsAccountRowViewModel> Accounts { get; } = new();
    public IReadOnlyList<int> RefreshMinuteOptions { get; } = Enumerable.Range(1, 60).ToArray();
    public IReadOnlyList<HourChoice> HourOptions { get; } = Enumerable.Range(0, 24)
        .Select(hour => new HourChoice(hour, $"{hour:00}:00")).ToArray();
    public ICommand AddAccountCommand { get; }
    public ICommand TestConnectionCommand { get; }
    public ICommand CancelCommand { get; }

    public bool AlwaysOnTop { get => _alwaysOnTop; set => SetProperty(ref _alwaysOnTop, value); }
    public bool ClickThrough { get => _clickThrough; set => SetProperty(ref _clickThrough, value); }
    public bool StartWithWindows { get => _startWithWindows; set => SetProperty(ref _startWithWindows, value); }
    public bool MinimizeToTray { get => _minimizeToTray; set => SetProperty(ref _minimizeToTray, value); }
    public bool ComfortableDensity { get => _comfortableDensity; set => SetProperty(ref _comfortableDensity, value); }
    public bool OpaqueBackground { get => _opaqueBackground; set => SetProperty(ref _opaqueBackground, value); }
    public int WidgetOpacityPercent { get => _widgetOpacityPercent; set => SetProperty(ref _widgetOpacityPercent, value); }
    public int RefreshIntervalMinutes { get => _refreshIntervalMinutes; set => SetProperty(ref _refreshIntervalMinutes, value); }
    public bool NotificationsEnabled { get => _notificationsEnabled; set => SetProperty(ref _notificationsEnabled, value); }
    public bool NotifyLowQuota { get => _notifyLowQuota; set => SetProperty(ref _notifyLowQuota, value); }
    public bool NotifyAuthenticationExpired { get => _notifyAuthenticationExpired; set => SetProperty(ref _notifyAuthenticationExpired, value); }
    public bool NotifyQuotaRecovered { get => _notifyQuotaRecovered; set => SetProperty(ref _notifyQuotaRecovered, value); }
    public int LowQuotaThresholdPercent { get => _lowQuotaThresholdPercent; set => SetProperty(ref _lowQuotaThresholdPercent, value); }
    public bool QuietHoursEnabled { get => _quietHoursEnabled; set => SetProperty(ref _quietHoursEnabled, value); }
    public int QuietHoursStartHour { get => _quietHoursStartHour; set => SetProperty(ref _quietHoursStartHour, value); }
    public int QuietHoursEndHour { get => _quietHoursEndHour; set => SetProperty(ref _quietHoursEndHour, value); }
    public string? CodexExecutablePath
    {
        get => _codexExecutablePath;
        set
        {
            if (SetProperty(ref _codexExecutablePath, value))
            {
                CodexExecutableStatus = "Path changed. Test connection to verify this installation.";
                DetectedCodexVersion = "Not checked";
                DetectedCodexPath = null;
            }
        }
    }
    public string CodexExecutableStatus { get => _codexExecutableStatus; private set => SetProperty(ref _codexExecutableStatus, value); }
    public string DetectedCodexVersion { get => _detectedCodexVersion; private set => SetProperty(ref _detectedCodexVersion, value); }
    public string? DetectedCodexPath { get => _detectedCodexPath; private set => SetProperty(ref _detectedCodexPath, value); }
    public bool HasStoredApiKey
    {
        get
        {
            try { return _credentials.HasApiKey(); }
            catch { return false; }
        }
    }
    public string ApiCredentialLabel => HasStoredApiKey ? "A legacy API key is stored in Windows Credential Manager." : "No legacy API key is stored.";
    public string Status { get => _status; private set => SetProperty(ref _status, value); }
    public bool CanEdit => !IsSaving && !_isUpdatingAccounts;
    public bool IsSaving
    {
        get => _isSaving;
        private set
        {
            if (SetProperty(ref _isSaving, value)) RaiseEditingState();
        }
    }
    public bool IsTestingConnection
    {
        get => _isTestingConnection;
        private set
        {
            if (SetProperty(ref _isTestingConnection, value))
                (TestConnectionCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        }
    }

    public async Task InitializeAsync()
    {
        var original = await _repository.LoadAsync().ConfigureAwait(true);
        AlwaysOnTop = original.AlwaysOnTop;
        ClickThrough = original.ClickThrough;
        try { StartWithWindows = await _startup.IsEnabledAsync(); }
        catch { StartWithWindows = original.StartWithWindows; }
        _initialStartupEnabled = StartWithWindows;
        MinimizeToTray = original.MinimizeToTray;
        ComfortableDensity = original.ComfortableDensity;
        OpaqueBackground = original.OpaqueBackground;
        WidgetOpacityPercent = original.WidgetOpacityPercent;
        RefreshIntervalMinutes = original.RefreshIntervalMinutes;
        NotificationsEnabled = original.NotificationsEnabled;
        NotifyLowQuota = original.NotifyLowQuota;
        NotifyAuthenticationExpired = original.NotifyAuthenticationExpired;
        NotifyQuotaRecovered = original.NotifyQuotaRecovered;
        LowQuotaThresholdPercent = original.LowQuotaThresholdPercent;
        QuietHoursEnabled = original.QuietHoursEnabled;
        QuietHoursStartHour = original.QuietHoursStartHour;
        QuietHoursEndHour = original.QuietHoursEndHour;
        CodexExecutablePath = original.CodexExecutablePath;
        ReloadAccounts();
        OnPropertyChanged(nameof(HasStoredApiKey));
        OnPropertyChanged(nameof(ApiCredentialLabel));
        await InspectInstallationAsync(testAppServer: false).ConfigureAwait(true);
    }

    public async Task<bool> SaveAsync()
    {
        if (!CanEdit) return false;
        if (WidgetOpacityPercent is < 35 or > 100)
        {
            Status = "Widget background opacity must be between 35% and 100%.";
            return false;
        }
        if (RefreshIntervalMinutes is < 1 or > 60)
        {
            Status = "Refresh interval must be between 1 and 60 minutes.";
            return false;
        }
        if (LowQuotaThresholdPercent is < 1 or > 99)
        {
            Status = "Low-quota threshold must be between 1% and 99% remaining.";
            return false;
        }
        if (QuietHoursStartHour is < 0 or > 23 || QuietHoursEndHour is < 0 or > 23
            || (QuietHoursEnabled && QuietHoursStartHour == QuietHoursEndHour))
        {
            Status = "Choose different quiet-hours start and end times between 00:00 and 23:00.";
            return false;
        }

        IsSaving = true;
        var startupChanged = false;
        try
        {
            if (StartWithWindows != _initialStartupEnabled)
            {
                await _startup.SetEnabledAsync(StartWithWindows);
                startupChanged = true;
            }

            await _repository.UpdateAsync(current => current with
            {
                AlwaysOnTop = AlwaysOnTop,
                ClickThrough = ClickThrough,
                StartWithWindows = StartWithWindows,
                MinimizeToTray = MinimizeToTray,
                ComfortableDensity = ComfortableDensity,
                OpaqueBackground = OpaqueBackground,
                WidgetOpacityPercent = WidgetOpacityPercent,
                RefreshIntervalMinutes = RefreshIntervalMinutes,
                NotificationsEnabled = NotificationsEnabled,
                NotifyLowQuota = NotifyLowQuota,
                NotifyAuthenticationExpired = NotifyAuthenticationExpired,
                NotifyQuotaRecovered = NotifyQuotaRecovered,
                LowQuotaThresholdPercent = LowQuotaThresholdPercent,
                QuietHoursEnabled = QuietHoursEnabled,
                QuietHoursStartHour = QuietHoursStartHour,
                QuietHoursEndHour = QuietHoursEndHour,
                CodexExecutablePath = string.IsNullOrWhiteSpace(CodexExecutablePath) ? null : CodexExecutablePath.Trim(),
                ApiFallback = current.ApiFallback with { Enabled = false }
            }).ConfigureAwait(true);
            _initialStartupEnabled = StartWithWindows;
            Status = "Settings saved.";
            RequestClose?.Invoke(this, true);
            return true;
        }
        catch (Exception exception)
        {
            Status = exception is StartupPreferenceException ? exception.Message : "Settings could not be saved. Check access to the local settings folder and try again.";
            if (startupChanged)
            {
                try { await _startup.SetEnabledAsync(_initialStartupEnabled); }
                catch { Status = "Settings could not be saved. The Windows startup preference also needs to be checked before retrying."; }
            }
            return false;
        }
        finally { IsSaving = false; }
    }

    public async Task TestConnectionAsync() => await InspectInstallationAsync(testAppServer: true).ConfigureAwait(true);

    private async Task InspectInstallationAsync(bool testAppServer)
    {
        if (IsTestingConnection) return;
        var testedPath = CodexExecutablePath;
        IsTestingConnection = true;
        CodexExecutableStatus = testAppServer ? "Checking the signed executable and App Server support…" : "Detecting the signed Codex executable…";
        try
        {
            var result = await _installationProbe.InspectAsync(testedPath, testAppServer, _lifetime.Token).ConfigureAwait(true);
            if (!string.Equals(testedPath, CodexExecutablePath, StringComparison.Ordinal)) return;
            CodexExecutableStatus = result.Message;
            DetectedCodexVersion = string.IsNullOrWhiteSpace(result.Version) ? "Not reported" : result.Version;
            DetectedCodexPath = result.ExecutablePath;
        }
        catch (OperationCanceledException) { }
        catch { CodexExecutableStatus = "The installation check could not finish. Browse to the official codex.exe and try again."; }
        finally { IsTestingConnection = false; }
    }

    public void CancelPendingOperations() => _lifetime.Cancel();

    public async Task RemoveStoredApiKeyAsync()
    {
        if (!CanEdit) return;
        try
        {
            _credentials.DeleteApiKey();
            await _repository.UpdateAsync(current => current with
            {
                ApiFallback = current.ApiFallback with { Enabled = false, HasStoredCredential = false }
            }).ConfigureAwait(true);
            OnPropertyChanged(nameof(HasStoredApiKey));
            OnPropertyChanged(nameof(ApiCredentialLabel));
            Status = "Legacy API key removed.";
        }
        catch { Status = "The legacy API key could not be removed. Try again after closing other credential tools."; }
    }

    private Task AddAccountAsync() => RunAccountActionAsync(async () =>
    {
        var used = _monitor.Accounts.Select(static account => account.Profile.Slot).ToHashSet();
        var slot = 1;
        while (used.Contains(slot)) slot = checked(slot + 1);
        if (slot > 0) await _interaction.EnrollAccountAsync(slot).ConfigureAwait(true);
    });

    private async Task RunAccountActionAsync(Func<Task> action)
    {
        if (!CanEdit) return;
        _isUpdatingAccounts = true;
        RaiseEditingState();
        try
        {
            await action().ConfigureAwait(true);
            ReloadAccounts();
        }
        catch (InvalidOperationException exception) { Status = exception.Message; }
        catch (ArgumentException exception) { Status = exception.Message; }
        catch { Status = "The account change could not be completed. Refresh the account and try again."; }
        finally
        {
            _isUpdatingAccounts = false;
            RaiseEditingState();
        }
    }

    private void ReloadAccounts()
    {
        Accounts.Clear();
        foreach (var account in _monitor.Accounts.OrderBy(value => value.Profile.Slot))
        {
            var captured = account;
            Accounts.Add(new SettingsAccountRowViewModel(captured,
                () => RunAccountActionAsync(async () => { await _interaction.ReauthenticateAsync(captured.Profile.Id).ConfigureAwait(true); }),
                () => RunAccountActionAsync(async () =>
                {
                    if (await _interaction.ConfirmRemoveAsync(captured.Profile.Alias).ConfigureAwait(true))
                    {
                        await _monitor.RemoveAsync(captured.Profile.Id).ConfigureAwait(true);
                        Status = "Account removed from this monitor.";
                    }
                }),
                alias => RunAccountActionAsync(async () =>
                {
                    await _monitor.RenameAccountAsync(captured.Profile.Id, alias).ConfigureAwait(true);
                    Status = "Account alias updated.";
                }),
                slot => RunAccountActionAsync(async () =>
                {
                    await _monitor.MoveAccountAsync(captured.Profile.Id, slot).ConfigureAwait(true);
                    Status = "Account order updated.";
                })));
        }
        (AddAccountCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
    }

    private void RaiseEditingState()
    {
        OnPropertyChanged(nameof(CanEdit));
        (AddAccountCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        (TestConnectionCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        (CancelCommand as RelayCommand)?.RaiseCanExecuteChanged();
    }
}

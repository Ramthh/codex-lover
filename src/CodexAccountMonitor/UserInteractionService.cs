using System.Windows;
using CodexAccountMonitor.Infrastructure;
using CodexAccountMonitor.ViewModels;
using CodexAccountMonitor.Infrastructure.DesktopSwitching;

namespace CodexAccountMonitor;

public sealed class UserInteractionService : IUserInteractionService
{
    private readonly AccountMonitorService _monitor;
    private readonly SettingsRepository _settings;
    private readonly CredentialManagerSecretStore _credentials;
    private readonly StartupRegistrationService _startup;
    private readonly Func<MainWindow?> _owner;
    private SettingsWindow? _settingsWindow;
    private readonly DesktopAccountSwitchService? _desktopSwitcher;
    private readonly Func<IDisposable?>? _suspendForDialog;

    public UserInteractionService(
        AccountMonitorService monitor,
        SettingsRepository settings,
        CredentialManagerSecretStore credentials,
        StartupRegistrationService startup,
        Func<MainWindow?> owner,
        DesktopAccountSwitchService? desktopSwitcher = null,
        Func<IDisposable?>? suspendForDialog = null)
    {
        _monitor = monitor;
        _settings = settings;
        _credentials = credentials;
        _startup = startup;
        _owner = owner;
        _desktopSwitcher = desktopSwitcher;
        _suspendForDialog = suspendForDialog;
    }

    public Task<bool> SwitchDesktopAccountAsync(Guid profileId)
    {
        var account = _monitor.Accounts.FirstOrDefault(x => x.Profile.Id == profileId);
        if (_desktopSwitcher is null || account is null) return Task.FromResult(false);
        using var modelSuppression = _suspendForDialog?.Invoke();
        var window = new DesktopSwitchWindow(_desktopSwitcher, profileId, account.Profile.Alias) { Owner = _owner() };
        return Task.FromResult(window.ShowDialog() == true);
    }

    public Task<bool> EnrollAccountAsync(int slot)
    {
        using var modelSuppression = _suspendForDialog?.Invoke();
        var viewModel = new AccountConnectionViewModel(_monitor, slot, setup: new CodexSetupViewModel(_settings));
        var window = new AccountConnectionWindow(viewModel) { Owner = _owner() };
        return Task.FromResult(window.ShowDialog() == true);
    }

    public Task<bool> ReauthenticateAsync(Guid profileId)
    {
        var account = _monitor.Accounts.FirstOrDefault(value => value.Profile.Id == profileId);
        if (account is null)
        {
            return Task.FromResult(false);
        }

        using var modelSuppression = _suspendForDialog?.Invoke();
        var viewModel = new AccountConnectionViewModel(
            _monitor,
            account.Profile.Slot,
            account.Profile.Alias,
            profileId,
            new CodexSetupViewModel(_settings));
        var window = new AccountConnectionWindow(viewModel) { Owner = _owner() };
        return Task.FromResult(window.ShowDialog() == true);
    }

    public Task<bool> ConfirmRemoveAsync(string alias)
    {
        using var modelSuppression = _suspendForDialog?.Invoke();
        var result = MessageBox.Show(
            _owner(),
            $"Remove {alias} from this monitor?\n\nIts isolated local Codex profile directory will be deleted. This does not delete the ChatGPT account.",
            "Remove account",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning,
            MessageBoxResult.No);
        return Task.FromResult(result == MessageBoxResult.Yes);
    }

    public Task<bool> ConfirmResetCreditAsync(string alias)
    {
        using var modelSuppression = _suspendForDialog?.Invoke();
        var result = MessageBox.Show(
            _owner(),
            $"Use one earned rate-limit reset for {alias}?\n\nThis action is sent to Codex and cannot be undone by the monitor.",
            "Use reset credit",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question,
            MessageBoxResult.No);
        return Task.FromResult(result == MessageBoxResult.Yes);
    }

    public async void ShowSettings()
    {
        if (_settingsWindow is { IsVisible: true })
        {
            _settingsWindow.Activate();
            return;
        }

        var viewModel = new SettingsViewModel(_settings, _monitor, _credentials, _startup, this);
        await viewModel.InitializeAsync().ConfigureAwait(true);
        using var modelSuppression = _suspendForDialog?.Invoke();
        _settingsWindow = new SettingsWindow(viewModel) { Owner = _owner() };
        bool saved;
        try { saved = _settingsWindow.ShowDialog() == true; }
        finally { _settingsWindow = null; }
        if (saved && Application.Current is App app)
        {
            await app.ReloadAndApplySettingsAsync().ConfigureAwait(true);
        }
    }

    public void ShowError(string title, string message)
    {
        using var modelSuppression = _suspendForDialog?.Invoke();
        MessageBox.Show(_owner(), message, title, MessageBoxButton.OK, MessageBoxImage.Error);
    }
}

public sealed class DemoInteractionService : IUserInteractionService
{
    private readonly Func<MainWindow?> _owner;
    private readonly Func<IDisposable?>? _suspendForDialog;

    public DemoInteractionService(Func<MainWindow?> owner, Func<IDisposable?>? suspendForDialog = null)
    {
        _owner = owner;
        _suspendForDialog = suspendForDialog;
    }

    public Task<bool> EnrollAccountAsync(int slot) => Task.FromResult(false);
    public Task<bool> ReauthenticateAsync(Guid profileId) => Task.FromResult(false);
    public Task<bool> ConfirmRemoveAsync(string alias) => Task.FromResult(false);
    public Task<bool> ConfirmResetCreditAsync(string alias) => Task.FromResult(false);

    public void ShowSettings()
    {
        using var modelSuppression = _suspendForDialog?.Invoke();
        MessageBox.Show(_owner(),
            "Settings are read-only in the sample-data preview. Launch without --demo to manage real accounts.",
            "Demo mode", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    public void ShowError(string title, string message)
    {
        using var modelSuppression = _suspendForDialog?.Invoke();
        MessageBox.Show(_owner(), message, title, MessageBoxButton.OK, MessageBoxImage.Error);
    }
}

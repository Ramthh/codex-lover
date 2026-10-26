using System.Diagnostics;
using System.Windows;
using System.Windows.Input;
using CodexAccountMonitor.Infrastructure;

namespace CodexAccountMonitor.ViewModels;

public sealed class AccountConnectionViewModel : ObservableObject, IDisposable
{
    private readonly Func<string, bool, Func<EnrollmentProgress, Task>, CancellationToken, Task> _connect;
    private readonly Action<Uri> _openBrowser;
    private readonly Action<string> _copyText;
    private readonly Func<Action, Task> _dispatch;
    private readonly Guid? _profileId;
    private readonly CancellationTokenSource _lifetime = new();
    private string _alias;
    private bool _useDeviceCode;
    private string _status = "Choose a sign-in method, then connect.";
    private string? _userCode;
    private bool _isBusy;
    private Uri? _authorizationUri;
    private bool _disposed;

    public AccountConnectionViewModel(
        AccountMonitorService monitor,
        int slot,
        string? alias = null,
        Guid? profileId = null,
        CodexSetupViewModel? setup = null)
        : this(async (name, deviceCode, progress, token) =>
        {
            if (profileId is Guid id)
                await monitor.ReauthenticateAsync(id, deviceCode, progress, token);
            else
                await monitor.EnrollAsync(name, slot, deviceCode, progress, token);
        }, slot, alias, profileId, setup,
        uri => Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true }),
        Clipboard.SetText,
        action => Application.Current.Dispatcher.InvokeAsync(action).Task)
    {
    }

    internal AccountConnectionViewModel(
        Func<string, bool, Func<EnrollmentProgress, Task>, CancellationToken, Task> connect,
        int slot, string? alias, Guid? profileId, CodexSetupViewModel? setup,
        Action<Uri> openBrowser, Action<string> copyText, Func<Action, Task> dispatch)
    {
        _connect = connect;
        _openBrowser = openBrowser;
        _copyText = copyText;
        _dispatch = dispatch;
        Slot = slot;
        _profileId = profileId;
        _alias = alias ?? (slot == 1 ? "Personal" : $"Account {slot}");
        Setup = setup;
        if (Setup is not null) Setup.PropertyChanged += OnSetupChanged;
        ConnectCommand = new AsyncRelayCommand(ConnectAsync, CanConnect);
        CancelCommand = new RelayCommand(Cancel, () => !_disposed);
        CopyCodeCommand = new RelayCommand(CopyCode, () => HasUserCode);
        OpenBrowserCommand = new RelayCommand(OpenBrowser, () => _authorizationUri is not null);
        CopyLinkCommand = new RelayCommand(CopyLink, () => HasBrowserLink);
    }

    public event EventHandler<bool>? RequestClose;
    public CodexSetupViewModel? Setup { get; }
    public bool HasSetup => Setup is not null;
    public async Task InitializeAsync() { if (Setup is not null) await Setup.CheckAsync(); }
    private void OnSetupChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs args) =>
        (ConnectCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();

    public int Slot { get; }
    public string SlotLabel => $"Account {Slot:00}";
    public bool IsReauthentication => _profileId is not null;
    public string Heading => IsReauthentication ? "Re-authenticate account" : "Add Codex account";
    public string ConnectLabel => IsBusy ? "Waiting for sign-in…" : IsReauthentication ? "Sign in again" : "Connect account";
    public bool HasBrowserLink => _authorizationUri is not null;
    public string VerificationSite => _authorizationUri is null ? string.Empty : $"Sign-in page: {_authorizationUri.Host}";

    public string Alias
    {
        get => _alias;
        set
        {
            if (SetProperty(ref _alias, value))
            {
                (ConnectCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
            }
        }
    }

    public bool UseDeviceCode
    {
        get => _useDeviceCode;
        set => SetProperty(ref _useDeviceCode, value);
    }

    public string Status
    {
        get => _status;
        private set => SetProperty(ref _status, value);
    }

    public string? UserCode
    {
        get => _userCode;
        private set
        {
            if (SetProperty(ref _userCode, value))
            {
                OnPropertyChanged(nameof(HasUserCode));
                (CopyCodeCommand as RelayCommand)?.RaiseCanExecuteChanged();
            }
        }
    }

    public bool HasUserCode => !string.IsNullOrWhiteSpace(UserCode);

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (SetProperty(ref _isBusy, value))
            {
                (ConnectCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
                OnPropertyChanged(nameof(ConnectLabel));
            }
        }
    }

    public ICommand ConnectCommand { get; }
    public ICommand CancelCommand { get; }
    public ICommand CopyCodeCommand { get; }
    public ICommand OpenBrowserCommand { get; }
    public ICommand CopyLinkCommand { get; }

    private bool CanConnect() => !_disposed && !IsBusy && Setup?.IsChecking != true && Setup?.IsReady != false
        && (IsReauthentication || !string.IsNullOrWhiteSpace(Alias));

    private void Cancel()
    {
        if (_disposed) return;
        Dispose();
        RequestClose?.Invoke(this, false);
    }

    private void CopyCode()
    {
        if (!HasUserCode) return;
        try { _copyText(UserCode!); Status = "Code copied. Enter it on the sign-in page, then return here."; }
        catch { Status = "The clipboard is busy. Select the code and copy it, or try again."; }
    }

    private void CopyLink()
    {
        if (_authorizationUri is null) return;
        try { _copyText(_authorizationUri.AbsoluteUri); Status = "Sign-in link copied. Paste it into your browser and complete sign-in, then return here."; }
        catch { Status = "The clipboard is busy. Try Copy sign-in link again, or select Reopen browser."; }
    }

    private void OpenBrowser()
    {
        if (_authorizationUri is null) return;
        try { _openBrowser(_authorizationUri); }
        catch { Status = "The browser could not open. Select Copy sign-in link and paste it into your browser, or select Reopen browser to retry."; }
    }

    internal async Task ConnectAsync()
    {
        if (!CanConnect()) return;
        var cancellationToken = _lifetime.Token;
        IsBusy = true;
        UserCode = null;
        SetAuthorizationUri(null);
        Status = "Starting an isolated Codex sign-in…";
        try
        {
            // Recheck immediately before authentication: the installation may have changed
            // since the dialog opened. A failed prerequisite must not start an account flow.
            if (Setup is not null)
            {
                var ready = await Setup.CheckAsync();
                cancellationToken.ThrowIfCancellationRequested();
                if (!ready)
                {
                    Status = "Finish step 1 above, then connect your Codex account.";
                    return;
                }
            }
            cancellationToken.ThrowIfCancellationRequested();
            await _connect(Alias, UseDeviceCode, ReportProgressAsync, cancellationToken).ConfigureAwait(true);
            cancellationToken.ThrowIfCancellationRequested();

            Status = "Account verified and connected.";
            RequestClose?.Invoke(this, true);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            Status = "Sign-in cancelled.";
        }
        catch (Exception exception)
        {
            Status = AccountConnectionErrors.Describe(exception);
        }
        finally
        {
            // A finished or failed attempt no longer owns a valid login session.
            // Do not offer an expired link/code after its isolated server has stopped.
            UserCode = null;
            SetAuthorizationUri(null);
            IsBusy = false;
            if (_disposed) _lifetime.Dispose();
        }
    }

    private async Task ReportProgressAsync(EnrollmentProgress progress)
    {
        await _dispatch(() =>
        {
            // A queued progress notification can arrive after the dialog was closed.
            if (_disposed) return;
            if (progress.Stage == "starting")
            {
                Status = "Starting Codex App Server…";
                return;
            }

            if (progress.Prompt is null)
            {
                return;
            }

            UserCode = progress.Prompt.UserCode;
            SetAuthorizationUri(progress.Prompt.AuthorizationUri ?? progress.Prompt.VerificationUri);
            Status = progress.Prompt.Type == "chatgptDeviceCode"
                ? "Enter the code below in the browser. This window will close after your account is verified."
                : "Complete sign-in in your browser. This window will close after your account is verified.";
            OpenBrowser();
        });
    }

    private void SetAuthorizationUri(Uri? uri)
    {
        _authorizationUri = uri;
        OnPropertyChanged(nameof(HasBrowserLink));
        OnPropertyChanged(nameof(VerificationSite));
        (OpenBrowserCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (CopyLinkCommand as RelayCommand)?.RaiseCanExecuteChanged();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (Setup is not null) { Setup.PropertyChanged -= OnSetupChanged; Setup.Dispose(); }
        _lifetime.Cancel();
        UserCode = null;
        SetAuthorizationUri(null);
        (ConnectCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        (CancelCommand as RelayCommand)?.RaiseCanExecuteChanged();
        // The in-flight operation may still register cancellation callbacks.
        if (!IsBusy) _lifetime.Dispose();
    }
}

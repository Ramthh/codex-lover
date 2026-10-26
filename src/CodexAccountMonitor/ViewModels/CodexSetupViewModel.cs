using System.Diagnostics;
using System.Windows.Input;
using CodexAccountMonitor.Infrastructure;

namespace CodexAccountMonitor.ViewModels;

/// <summary>Checks the local prerequisite without reading account credentials or starting sign-in.</summary>
public sealed class CodexSetupViewModel : ObservableObject, IDisposable
{
    private readonly ICodexInstallationProbe _probe;
    private readonly Func<CancellationToken, Task<string?>> _loadPath;
    private readonly Func<string?, CancellationToken, Task> _savePath;
    private readonly CancellationTokenSource _lifetime = new();
    private string? _configuredPath;
    private bool _loaded;
    private bool _pendingPathChange;
    private bool _isChecking;
    private bool _isReady;
    private string _message = "Checking Codex connection support…";

    public CodexSetupViewModel(SettingsRepository settings) : this(new CodexInstallationProbe(),
        async token => (await settings.LoadAsync(token)).CodexExecutablePath,
        async (path, token) => { await settings.UpdateAsync(value => value with { CodexExecutablePath = path }, token); }) { }

    internal CodexSetupViewModel(ICodexInstallationProbe probe,
        Func<CancellationToken, Task<string?>> loadPath, Func<string?, CancellationToken, Task> savePath)
    {
        _probe = probe;
        _loadPath = loadPath;
        _savePath = savePath;
        RetryCommand = new AsyncRelayCommand(() => CheckAsync(), () => !IsChecking);
        AutoDetectCommand = new AsyncRelayCommand(() => SelectExecutableAsync(null), () => !IsChecking);
        InstallGuideCommand = new RelayCommand(OpenInstallationGuide);
    }

    public ICommand RetryCommand { get; }
    public ICommand AutoDetectCommand { get; }
    public ICommand InstallGuideCommand { get; }
    public bool IsChecking { get => _isChecking; private set { if (SetProperty(ref _isChecking, value)) { OnPropertyChanged(nameof(CanChoosePath)); (RetryCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged(); (AutoDetectCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged(); } } }
    public bool CanChoosePath => !IsChecking;
    public bool IsReady { get => _isReady; private set => SetProperty(ref _isReady, value); }
    public string Message { get => _message; private set => SetProperty(ref _message, value); }

    public Task<bool> CheckAsync() => CheckCoreAsync(changePath: false, null);
    public Task<bool> SelectExecutableAsync(string? path) => CheckCoreAsync(changePath: true, path);

    private async Task<bool> CheckCoreAsync(bool changePath, string? path)
    {
        if (IsChecking || _lifetime.IsCancellationRequested) return false;
        IsChecking = true;
        IsReady = false;
        Message = "Checking Codex connection support…";
        try
        {
            if (!_loaded) { _configuredPath = await _loadPath(_lifetime.Token); _loaded = true; }
            if (changePath)
            {
                _configuredPath = string.IsNullOrWhiteSpace(path) ? null : path;
                _pendingPathChange = true;
            }
            var result = await _probe.InspectAsync(_configuredPath, testAppServer: true, _lifetime.Token);
            _lifetime.Token.ThrowIfCancellationRequested();
            // Do not replace a previously working setting with an invalid file selection.
            // Retain the attempted selection in this window so Retry cannot silently use another file.
            if (result.IsReady && _pendingPathChange)
            {
                await _savePath(_configuredPath, _lifetime.Token);
                _lifetime.Token.ThrowIfCancellationRequested();
                _pendingPathChange = false;
            }
            IsReady = result.IsReady;
            Message = result.IsReady
                ? "Ready to connect. Give this account a name below, then choose Connect account and complete the separate Codex sign-in."
                : result.Message;
            return IsReady;
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { return false; }
        catch
        {
            Message = "The Codex check could not finish or save its setting. Retry, or choose the official installation with Browse.";
            return false;
        }
        finally { IsChecking = false; }
    }

    private void OpenInstallationGuide()
    {
        try { Process.Start(new ProcessStartInfo("https://learn.chatgpt.com/docs/codex/cli") { UseShellExecute = true }); }
        catch { Message = "Repair or reinstall Codex Lover, then choose Retry check. For a separate official Codex CLI installation, see learn.chatgpt.com/docs/codex/cli."; }
    }

    public void Dispose() { _lifetime.Cancel(); }
}

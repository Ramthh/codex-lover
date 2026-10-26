using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using CodexAccountMonitor.Infrastructure.AppServer;
using CodexAccountMonitor.Infrastructure.DesktopSwitching;

namespace CodexAccountMonitor;

public sealed class DesktopSwitchWindow : Window
{
    private readonly Func<Task<CodexDesktopState>> _inspect;
    private readonly Func<CancellationToken, IProgress<DesktopSwitchProgress>, Task> _switch;
    private readonly Func<Task>? _openCodex;
    private readonly TextBlock _stage;
    private readonly TextBlock _status;
    private readonly ProgressBar _progress;
    private readonly Button _switchButton;
    private readonly Button _cancel;
    private readonly Button _openButton;
    private readonly System.Windows.Threading.DispatcherTimer _waitTimer;
    private DateTimeOffset? _waitingSince;
    private CancellationTokenSource? _operation;
    private bool _working;
    private bool _closed;
    private bool _canCancel;
    private bool _finishingSignIn;
    private bool _signInChangeStarted;
    private bool _closeAfterCancellation;
    private bool _hasStartedSwitch;

    public DesktopSwitchWindow(DesktopAccountSwitchService service, Guid profileId, string alias)
        : this(alias, service.InspectAsync, (token, progress) => service.SwitchAndReopenAsync(profileId, token, progress), service.OpenCodexAsync)
    {
    }

    // Synthetic operations let UI tests exercise cancellation without touching Codex or account files.
    internal DesktopSwitchWindow(string alias, Func<Task<CodexDesktopState>> inspect,
        Func<CancellationToken, IProgress<DesktopSwitchProgress>, Task> switchAccount, Func<Task>? openCodex = null)
    {
        _inspect = inspect;
        _switch = switchAccount;
        _openCodex = openCodex;
        _waitTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _waitTimer.Tick += OnWaitTimerTick;
        var workArea = SystemParameters.WorkArea;
        var availableWidth = Math.Max(200, workArea.Width - 30);
        var availableHeight = Math.Max(200, workArea.Height - 30);
        Title = "Switch Codex account";
        Width = Math.Min(540, availableWidth);
        Height = Math.Min(440, availableHeight);
        MinWidth = Math.Min(360, availableWidth);
        MinHeight = Math.Min(320, availableHeight);
        MaxWidth = availableWidth;
        MaxHeight = availableHeight;
        ResizeMode = ResizeMode.CanResizeWithGrip;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        SetResourceReference(BackgroundProperty, "ShellBrush");
        SetResourceReference(ForegroundProperty, "PrimaryTextBrush");

        var layout = new Grid();
        layout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var panel = new StackPanel { Margin = new Thickness(24, 24, 24, 0) };
        panel.Children.Add(new TextBlock
        {
            Text = $"Use {alias} in Codex", FontSize = 22, FontWeight = FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap
        });
        panel.Children.Add(new TextBlock
        {
            Text = "We’ll close Codex, verify it has exited, switch your saved sign-in, and reopen it.",
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 14, 0, 10)
        });
        var warning = new TextBlock
        {
            Text = "Restarting Codex stops running tasks. Let them finish and save edits before continuing.",
            FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 12)
        };
        warning.SetResourceReference(TextBlock.ForegroundProperty, "WarningBrush");
        panel.Children.Add(warning);
        panel.Children.Add(new TextBlock
        {
            Text = "This changes the local Codex sign-in, including CLI sessions sharing it. In the combined ChatGPT app, its account changes too. New requests use the selected account’s available quota. Running tasks are not resumed automatically.",
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 20)
        });
        _stage = new TextBlock
        {
            Text = "Checking Codex", FontWeight = FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8)
        };
        _stage.SetResourceReference(TextBlock.ForegroundProperty, "MintBrush");
        AutomationProperties.SetName(_stage, "Account switch step");
        panel.Children.Add(_stage);
        _status = new TextBlock
        {
            Text = "Checking whether Codex is open…", TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 12), MinHeight = 50
        };
        _status.SetResourceReference(TextBlock.ForegroundProperty, "SecondaryTextBrush");
        AutomationProperties.SetName(_status, "Account switch status");
        AutomationProperties.SetLiveSetting(_status, AutomationLiveSetting.Polite);
        panel.Children.Add(_status);
        _progress = new ProgressBar { Height = 4, IsIndeterminate = true, Visibility = Visibility.Collapsed,
            Margin = new Thickness(0, 0, 0, 16) };
        AutomationProperties.SetName(_progress, "Switching Codex account");
        panel.Children.Add(_progress);

        var buttons = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(24, 12, 24, 16) };
        _cancel = new Button { Content = "Cancel", Padding = new Thickness(14, 7, 14, 7),
            Margin = new Thickness(0, 0, 10, 8) };
        _cancel.Click += (_, _) => CancelOrClose(closeWhenCancelled: false);
        _switchButton = new Button { Content = "Switch & reopen", Padding = new Thickness(14, 7, 14, 7),
            Margin = new Thickness(0, 0, 0, 8), IsDefault = true };
        _switchButton.Click += SwitchOnClick;
        _openButton = new Button { Content = "Open Codex", Padding = new Thickness(14, 7, 14, 7),
            Margin = new Thickness(0, 0, 10, 8), Visibility = Visibility.Collapsed };
        _openButton.Click += OpenCodexOnClick;
        buttons.Children.Add(_openButton);
        buttons.Children.Add(_cancel);
        buttons.Children.Add(_switchButton);
        var scroll = new ScrollViewer
        {
            Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
        };
        Grid.SetRow(scroll, 0);
        Grid.SetRow(buttons, 1);
        layout.Children.Add(scroll);
        layout.Children.Add(buttons);
        Content = layout;

        Closing += (_, e) =>
        {
            if (!_working) return;
            e.Cancel = true;
            if (!_finishingSignIn) _closeAfterCancellation = true;
            if (_canCancel) RequestCancellation(closeWhenCancelled: true);
        };
        Closed += (_, _) => { _closed = true; _waitTimer.Stop(); };
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key != Key.Escape) return;
            e.Handled = true;
            CancelOrClose(closeWhenCancelled: true);
        };
        Loaded += async (_, _) => await InspectAsync();
    }

    private async Task InspectAsync()
    {
        try
        {
            var state = await _inspect();
            if (_closed || _hasStartedSwitch) return;
            _stage.Text = state.Problem is null ? "Ready to switch" : "Codex needs attention";
            _status.Text = state.Problem ?? (state.CanSwitch
                ? "Codex is closed. Ready to verify the saved account and reopen it."
                : "Codex is open. When your tasks are finished, select Switch & reopen.");
        }
        catch
        {
            if (_closed || _hasStartedSwitch) return;
            _stage.Text = "Check again when ready";
            _status.Text = "The initial Codex check did not finish. Switch & reopen will check again before making changes.";
        }
    }

    private void CancelOrClose(bool closeWhenCancelled)
    {
        if (!_working) { Close(); return; }
        if (!_finishingSignIn && closeWhenCancelled) _closeAfterCancellation = true;
        if (_canCancel) RequestCancellation(closeWhenCancelled);
    }

    private void RequestCancellation(bool closeWhenCancelled)
    {
        _closeAfterCancellation |= closeWhenCancelled;
        _canCancel = false;
        _cancel.IsEnabled = false;
        _cancel.Content = "Stopping…";
        _status.Text = "Stopping before changing the sign-in…";
        _operation?.Cancel();
    }

    private void ShowProgress(DesktopSwitchProgress update, CancellationTokenSource operation)
    {
        if (_closed || !_working || _operation != operation) return;
        if (update.Stage == DesktopSwitchStage.WaitingForExit)
        {
            _waitingSince ??= DateTimeOffset.UtcNow;
            _waitTimer.Start();
        }
        else { _waitingSince = null; _waitTimer.Stop(); }
        _finishingSignIn |= update.Stage is DesktopSwitchStage.SwitchingSignIn or DesktopSwitchStage.ReopeningCodex or DesktopSwitchStage.Completed;
        _signInChangeStarted |= update.Stage is DesktopSwitchStage.SwitchingSignIn or DesktopSwitchStage.Completed;
        _canCancel = !_finishingSignIn && !operation.IsCancellationRequested;
        _cancel.IsEnabled = _canCancel;
        _cancel.ToolTip = _finishingSignIn ? "Finishing the sign-in change safely. Please wait." : null;
        _stage.Text = update.Stage switch
        {
            DesktopSwitchStage.Checking => "Step 1 of 4 · Check account",
            DesktopSwitchStage.ClosingCodex or DesktopSwitchStage.WaitingForExit => "Step 2 of 4 · Close Codex",
            DesktopSwitchStage.SwitchingSignIn => "Step 3 of 4 · Switch sign-in",
            DesktopSwitchStage.ReopeningCodex => "Step 4 of 4 · Reopen Codex",
            DesktopSwitchStage.Completed => "Switch complete",
            _ => "Switching Codex"
        };
        if (!operation.IsCancellationRequested || _finishingSignIn) _status.Text = update.Message;
    }

    private async void SwitchOnClick(object sender, RoutedEventArgs e)
    {
        if (_working || _closed) return;
        using var operation = new CancellationTokenSource();
        _operation = operation;
        _hasStartedSwitch = true;
        _working = true;
        _canCancel = true;
        _finishingSignIn = false;
        _signInChangeStarted = false;
        _closeAfterCancellation = false;
        _switchButton.IsEnabled = false;
        _openButton.Visibility = Visibility.Collapsed;
        _cancel.IsEnabled = true;
        _cancel.Content = "Cancel";
        _cancel.ToolTip = null;
        _progress.Visibility = Visibility.Visible;
        ShowProgress(new(DesktopSwitchStage.Checking, "Verifying the saved account and checking Codex…"), operation);
        var progress = new Progress<DesktopSwitchProgress>(update => ShowProgress(update, operation));
        try
        {
            await _switch(operation.Token, progress);
            _working = false;
            DialogResult = true;
        }
        catch (OperationCanceledException) when (operation.IsCancellationRequested)
        {
            _stage.Text = "Switch cancelled";
            _status.Text = _signInChangeStarted
                ? "The switch was interrupted. Check the current Codex sign-in before trying again."
                : "No account was switched. If Codex closed, you can reopen it when ready.";
        }
        catch (Exception exception)
        {
            _stage.Text = "Switch needs attention";
            _status.Text = exception switch
            {
                AppServerRpcException { IsAuthenticationFailure: true } => "The saved account needs sign-in again. Reconnect it from its account menu, then retry Switch & reopen.",
                AppServerRpcException { IsRateLimited: true } => "Codex is temporarily limiting account checks. Wait a moment and retry.",
                InvalidOperationException or InvalidDataException or TimeoutException => exception.Message,
                _ => "The account could not be switched. Check its sign-in and your connection, then try again."
            };
        }
        finally
        {
            _waitTimer.Stop();
            _waitingSince = null;
            _working = false;
            _canCancel = false;
            _operation = null;
            _progress.Visibility = Visibility.Collapsed;
            _switchButton.IsEnabled = true;
            _cancel.IsEnabled = true;
            _cancel.Content = "Close";
            _cancel.ToolTip = null;
            if (!_closed && _openCodex is not null) _openButton.Visibility = Visibility.Visible;
            if (_closeAfterCancellation && !_closed) Close();
        }
    }

    private void OnWaitTimerTick(object? sender, EventArgs e)
    {
        if (_waitingSince is not { } since || !_working || _operation?.IsCancellationRequested == true) return;
        var seconds = Math.Max(0, (int)(DateTimeOffset.UtcNow - since).TotalSeconds);
        _status.Text = seconds < 8
            ? $"Waiting for Codex to exit ({seconds}s of up to 60s). Your sign-in has not changed."
            : $"Still waiting for Codex to exit ({seconds}s of up to 60s). Check Codex for a confirmation or running task. You can cancel safely; your sign-in has not changed.";
    }

    private async void OpenCodexOnClick(object sender, RoutedEventArgs e)
    {
        if (_working || _closed || _openCodex is null) return;
        _working = true;
        _canCancel = false;
        _finishingSignIn = true;
        _openButton.IsEnabled = _switchButton.IsEnabled = _cancel.IsEnabled = false;
        _stage.Text = "Opening Codex";
        _status.Text = "Opening the Codex window with its current sign-in…";
        _progress.Visibility = Visibility.Visible;
        try
        {
            await _openCodex();
            _stage.Text = "Codex is open";
            _status.Text = "The Codex window is open. Opening it does not change the current sign-in.";
        }
        catch (Exception exception)
        {
            _stage.Text = "Codex needs attention";
            _status.Text = exception is InvalidOperationException ? exception.Message
                : "Codex could not be opened. Check its installation and retry.";
        }
        finally
        {
            _working = false;
            _finishingSignIn = false;
            _openButton.IsEnabled = _switchButton.IsEnabled = _cancel.IsEnabled = true;
            _progress.Visibility = Visibility.Collapsed;
        }
    }
}

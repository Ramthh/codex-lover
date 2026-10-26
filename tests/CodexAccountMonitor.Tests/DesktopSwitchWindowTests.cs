using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Threading;
using CodexAccountMonitor.Infrastructure.AppServer;
using CodexAccountMonitor.Infrastructure.DesktopSwitching;

namespace CodexAccountMonitor.Tests;

/// <summary>Exercises the modal dialog with synthetic operations; never reads or controls real Codex.</summary>
public sealed class DesktopSwitchWindowTests
{
    [Fact]
    public Task ClosedPreviewIsScrollableResizableAndRequiresExplicitSwitch() => OnStaAsync(async () =>
    {
        var calls = 0;
        var window = HiddenWindow(Ready, (_, _) => { calls++; return Task.CompletedTask; });
        await ShowAndDriveAsync(window, async () =>
        {
            await UntilAsync(() => Status(window).Text.Contains("Codex is closed"));
            var layout = Assert.IsType<Grid>(window.Content);
            var scroll = Assert.Single(layout.Children.OfType<ScrollViewer>());
            Assert.Equal(ResizeMode.CanResizeWithGrip, window.ResizeMode);
            Assert.True(window.Height <= SystemParameters.WorkArea.Height);
            Assert.True(window.Width <= SystemParameters.WorkArea.Width);
            Assert.Equal(0, calls);
            Assert.Contains(Descendants(window).OfType<TextBlock>(), text => text.Text.Contains("Restarting Codex stops running tasks"));
            Assert.True(SwitchButton(window).IsEnabled);
            window.Width = 400;
            window.Height = 330;
            window.UpdateLayout();
            Assert.True(scroll.ViewportHeight > 0);
            Assert.True(scroll.ExtentHeight > scroll.ViewportHeight);
            foreach (var button in new[] { SwitchButton(window), ButtonByText(window, "Cancel") })
            {
                var bounds = button.TransformToAncestor(layout).TransformBounds(new Rect(button.RenderSize));
                Assert.True(button.ActualHeight > 0);
                Assert.True(bounds.Top >= 0 && bounds.Bottom <= layout.ActualHeight,
                    "Switch and cancel must stay visible below the scrolling content at small window heights.");
            }
            window.Close();
        });
    });

    [Fact]
    public Task RunningPreviewUsesOneGuidedSwitchActionAndClosesOnSuccess() => OnStaAsync(async () =>
    {
        var calls = 0;
        var window = HiddenWindow(() => Task.FromResult(new CodexDesktopState(
            new("synthetic.exe", "Synthetic Codex"), [new(10, 1, "Codex", "synthetic.exe")], null)),
            (_, _) => { calls++; return Task.CompletedTask; });
        var result = await ShowAndDriveAsync(window, async () =>
        {
            await UntilAsync(() => Status(window).Text.Contains("Codex is open"));
            Assert.DoesNotContain("quit", Status(window).Text, StringComparison.OrdinalIgnoreCase);
            SwitchButton(window).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        });
        Assert.True(result);
        Assert.Equal(1, calls);
    });

    [Fact]
    public Task CancelWhileWaitingStopsOperationAndKeepsClearResultVisible() => OnStaAsync(async () =>
    {
        var cancelled = false;
        var window = HiddenWindow(Ready, async (token, progress) =>
        {
            progress.Report(new(DesktopSwitchStage.WaitingForExit, "Waiting for Codex to exit…"));
            try { await Task.Delay(Timeout.Infinite, token); }
            finally { cancelled = token.IsCancellationRequested; }
        });
        await ShowAndDriveAsync(window, async () =>
        {
            SwitchButton(window).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await UntilAsync(() => Stage(window).Text.StartsWith("Step 2"));
            var cancel = ButtonByText(window, "Cancel");
            Assert.True(cancel.IsEnabled);
            cancel.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await UntilAsync(() => SwitchButton(window).IsEnabled);
            Assert.True(cancelled);
            Assert.Contains("No account was switched", Status(window).Text);
            Assert.True(window.IsVisible);
            ButtonByText(window, "Close").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        });
    });

    [Fact]
    public Task CancelledSwitchThatReopensPreviousSignInStillReportsNoAccountWasChanged() => OnStaAsync(async () =>
    {
        var window = HiddenWindow(Ready, async (token, progress) =>
        {
            progress.Report(new(DesktopSwitchStage.WaitingForExit, "Waiting for Codex…"));
            try { await Task.Delay(Timeout.Infinite, token); }
            catch (OperationCanceledException)
            {
                progress.Report(new(DesktopSwitchStage.ReopeningCodex, "Reopening the previous sign-in…"));
                await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                throw;
            }
        });
        await ShowAndDriveAsync(window, async () =>
        {
            SwitchButton(window).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await UntilAsync(() => Stage(window).Text.StartsWith("Step 2"));
            ButtonByText(window, "Cancel").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await UntilAsync(() => SwitchButton(window).IsEnabled);
            Assert.Equal("Switch cancelled", Stage(window).Text);
            Assert.Contains("No account was switched", Status(window).Text);
            Assert.DoesNotContain("interrupted", Status(window).Text);
            window.Close();
        });
    });

    [Fact]
    public Task FailedSwitchOffersOpenOnlyRetryWithoutAnotherSwitch() => OnStaAsync(async () =>
    {
        var switches = 0;
        var opens = 0;
        var window = new DesktopSwitchWindow("Test account", Ready,
            (_, _) => { switches++; return Task.FromException(new DesktopReopenException("Select Open Codex to retry.", new IOException())); },
            () => { opens++; return Task.CompletedTask; })
        {
            ShowActivated = false, ShowInTaskbar = false, WindowStartupLocation = WindowStartupLocation.Manual,
            Left = -32000, Top = -32000
        };
        await ShowAndDriveAsync(window, async () =>
        {
            Assert.Equal(Visibility.Collapsed, ButtonByText(window, "Open Codex").Visibility);
            SwitchButton(window).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await UntilAsync(() => Stage(window).Text == "Switch needs attention");
            Assert.Equal(Visibility.Visible, ButtonByText(window, "Open Codex").Visibility);
            ButtonByText(window, "Open Codex").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await UntilAsync(() => Stage(window).Text == "Codex is open");
            Assert.Equal(1, switches);
            Assert.Equal(1, opens);
            window.Close();
        });
    });

    [Fact]
    public Task ClosingDuringCheckCancelsThenClosesWithoutReportingSuccess() => OnStaAsync(async () =>
    {
        var started = false;
        var cancelled = false;
        var window = HiddenWindow(Ready, async (token, _) =>
        {
            started = true;
            try { await Task.Delay(Timeout.Infinite, token); }
            finally { cancelled = token.IsCancellationRequested; }
        });
        var result = await ShowAndDriveAsync(window, async () =>
        {
            SwitchButton(window).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await UntilAsync(() => started);
            window.Close();
        });
        Assert.False(result);
        Assert.True(cancelled);
    });

    [Theory]
    [InlineData(DesktopSwitchStage.SwitchingSignIn, "Step 3")]
    [InlineData(DesktopSwitchStage.ReopeningCodex, "Step 4")]
    public Task CancelAndWindowCloseAreBlockedWhileFinishingSignIn(DesktopSwitchStage stage, string label) => OnStaAsync(async () =>
    {
        var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationToken operationToken = default;
        var window = HiddenWindow(Ready, async (token, progress) =>
        {
            operationToken = token;
            progress.Report(new(stage, "Finishing the switch…"));
            await finish.Task;
        });
        var result = await ShowAndDriveAsync(window, async () =>
        {
            try
            {
                SwitchButton(window).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                await UntilAsync(() => Stage(window).Text.StartsWith(label));
                Assert.False(ButtonByText(window, "Cancel").IsEnabled);
                window.Close();
                Assert.True(window.IsVisible);
                Assert.False(operationToken.IsCancellationRequested);
            }
            finally { finish.TrySetResult(); }
        });
        Assert.True(result);
    });

    [Fact]
    public Task CloseTimeoutShowsServiceExplanationAndAllowsRetry() => OnStaAsync(async () =>
    {
        const string message = "Codex did not exit in time. No sign-in was changed. Finish your tasks and try again.";
        var window = HiddenWindow(Ready, (_, _) => Task.FromException(new TimeoutException(message)));
        await ShowAndDriveAsync(window, async () =>
        {
            SwitchButton(window).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await UntilAsync(() => Stage(window).Text == "Switch needs attention");
            Assert.Equal(message, Status(window).Text);
            Assert.True(SwitchButton(window).IsEnabled);
            Assert.True(ButtonByText(window, "Close").IsEnabled);
            window.Close();
        });
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task TimeoutRecoveryBlocksCloseOnlyWhileOpeningAndClosesAfterCompletion(bool recoveryFails) => OnStaAsync(async () =>
    {
        var recovery = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var switches = 0;
        var opens = 0;
        var closed = 0;
        var window = new DesktopSwitchWindow("Test account", Ready,
            (_, _) =>
            {
                switches++;
                return Task.FromException(new TimeoutException("Synthetic desktop did not quit; sign-in unchanged."));
            },
            () => { opens++; return recovery.Task; })
        {
            ShowActivated = false, ShowInTaskbar = false, WindowStartupLocation = WindowStartupLocation.Manual,
            Left = -32000, Top = -32000
        };
        window.Closed += (_, _) => closed++;
        var result = await ShowAndDriveAsync(window, async () =>
        {
            try
            {
                SwitchButton(window).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                await UntilAsync(() => Stage(window).Text == "Switch needs attention");
                var close = ButtonByText(window, "Close");
                var open = ButtonByText(window, "Open Codex");
                var handle = new WindowInteropHelper(window).Handle;
                Assert.NotEqual(IntPtr.Zero, handle);
                Assert.True(IsWindowEnabled(handle));
                Assert.True(close.IsEnabled);

                open.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                await UntilAsync(() => Stage(window).Text == "Opening Codex");
                Assert.False(close.IsEnabled);
                Assert.False(open.IsEnabled);
                Assert.False(SwitchButton(window).IsEnabled);
                // A routed click exercises the guard even though normal input
                // cannot invoke this disabled button. Close() follows titlebar X's
                // same Closing event and must be refused during the awaited task.
                close.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                window.Close();
                Assert.True(window.IsVisible);
                Assert.Equal(0, closed);
                Assert.False(recovery.Task.IsCompleted);

                if (recoveryFails) recovery.SetException(new InvalidOperationException("Synthetic open failure."));
                else recovery.SetResult();
                await UntilAsync(() => Stage(window).Text == (recoveryFails ? "Codex needs attention" : "Codex is open")
                    && close.IsEnabled && open.IsEnabled && SwitchButton(window).IsEnabled);
                Assert.True(IsWindowEnabled(handle));
                Assert.True(window.IsVisible);
                Assert.Equal(0, closed);
                Assert.Equal(1, switches);
                Assert.Equal(1, opens);
                if (recoveryFails) Assert.Equal("Synthetic open failure.", Status(window).Text);
                else Assert.Contains("does not change the current sign-in", Status(window).Text);

                close.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Assert.False(window.IsVisible);
                Assert.False(IsWindow(handle));
                Assert.Equal(1, closed);
            }
            finally { recovery.TrySetResult(); }
        });
        Assert.False(result);
        Assert.Equal(1, closed);
    });

    [Fact]
    public Task DelayedInitialInspectionCannotOverwriteFailedSwitchStatus() => OnStaAsync(async () =>
    {
        var inspection = new TaskCompletionSource<CodexDesktopState>(TaskCreationOptions.RunContinuationsAsynchronously);
        var window = HiddenWindow(() => inspection.Task, (_, _) => Task.FromException(new InvalidOperationException("Synthetic switch problem")));
        await ShowAndDriveAsync(window, async () =>
        {
            SwitchButton(window).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await UntilAsync(() => Status(window).Text == "Synthetic switch problem");
            inspection.TrySetResult(await Ready());
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            Assert.Equal("Synthetic switch problem", Status(window).Text);
            window.Close();
        });
    });

    [Theory]
    [InlineData(true, false, "The saved account needs sign-in again. Reconnect it from its account menu, then retry Switch & reopen.")]
    [InlineData(false, true, "Codex is temporarily limiting account checks. Wait a moment and retry.")]
    public Task AuthenticationAndRateLimitFailuresExplainTheNextAction(bool authentication, bool rateLimited, string message) => OnStaAsync(async () =>
    {
        var window = HiddenWindow(Ready, (_, _) => Task.FromException(new AppServerRpcException(authentication ? 401 : 429, authentication, rateLimited)));
        await ShowAndDriveAsync(window, async () =>
        {
            SwitchButton(window).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await UntilAsync(() => Stage(window).Text == "Switch needs attention");
            Assert.Equal(message, Status(window).Text);
            Assert.True(SwitchButton(window).IsEnabled);
            window.Close();
        });
    });

    private static Task<CodexDesktopState> Ready() => Task.FromResult(new CodexDesktopState(
        new("synthetic.exe", "Synthetic Codex"), [], null));

    private static DesktopSwitchWindow HiddenWindow(Func<Task<CodexDesktopState>> inspect,
        Func<CancellationToken, IProgress<DesktopSwitchProgress>, Task> operation) => new("Test account", inspect, operation)
    {
        ShowActivated = false, ShowInTaskbar = false, WindowStartupLocation = WindowStartupLocation.Manual,
        Left = -32000, Top = -32000
    };

    private static TextBlock Status(Window window) => Descendants(window).OfType<TextBlock>()
        .Single(text => AutomationProperties.GetName(text) == "Account switch status");
    private static TextBlock Stage(Window window) => Descendants(window).OfType<TextBlock>()
        .Single(text => AutomationProperties.GetName(text) == "Account switch step");
    private static Button SwitchButton(Window window) => ButtonByText(window, "Switch & reopen");
    private static Button ButtonByText(Window window, string text) => Descendants(window).OfType<Button>()
        .Single(button => Equals(button.Content, text));

    private static IEnumerable<DependencyObject> Descendants(DependencyObject element)
    {
        foreach (var child in LogicalTreeHelper.GetChildren(element).OfType<DependencyObject>())
        {
            yield return child;
            foreach (var descendant in Descendants(child)) yield return descendant;
        }
    }

    private static async Task<bool?> ShowAndDriveAsync(Window window, Func<Task> drive)
    {
        Task? operation = null;
        Exception? driveError = null;
        var timedOut = false;
        var watchdog = new DispatcherTimer { Interval = TimeSpan.FromSeconds(12) };
        watchdog.Tick += (_, _) => { timedOut = true; watchdog.Stop(); window.Close(); };
        window.Loaded += (_, _) => operation = DriveAsync();
        async Task DriveAsync()
        {
            try { await drive(); }
            catch (Exception exception) { driveError = exception; window.Close(); }
        }
        watchdog.Start();
        try
        {
            var result = window.ShowDialog();
            if (operation is not null) await operation.WaitAsync(TimeSpan.FromSeconds(3));
            Assert.False(timedOut, "The synthetic switch dialog exceeded its test timeout.");
            if (driveError is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(driveError).Throw();
            return result;
        }
        finally { watchdog.Stop(); if (window.IsVisible) window.Close(); }
    }

    private static async Task UntilAsync(Func<bool> condition)
    {
        var timeout = DateTime.UtcNow.AddSeconds(8);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < timeout, "Expected switch dialog transition did not occur.");
            await Task.Delay(20);
        }
    }

    private static Task OnStaAsync(Func<Task> test)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
            dispatcher.BeginInvoke(new Action(async () =>
            {
                try { await test(); completion.TrySetResult(); }
                catch (Exception exception) { completion.TrySetException(exception); }
                finally { dispatcher.BeginInvokeShutdown(DispatcherPriority.Background); }
            }));
            Dispatcher.Run();
        }) { IsBackground = true, Name = "Synthetic desktop switch dialog regression" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return completion.Task.WaitAsync(TimeSpan.FromSeconds(20));
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool IsWindowEnabled(IntPtr window);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool IsWindow(IntPtr window);
}

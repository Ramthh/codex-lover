using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using CodexAccountMonitor.Infrastructure;
using CodexAccountMonitor.Infrastructure.AppServer;
using CodexAccountMonitor.ViewModels;

namespace CodexAccountMonitor.Tests;

public sealed class AccountConnectionWindowTests
{
    [Fact]
    public Task CompactDialogKeepsCancelReachableAndShowsManualSignInFallback() => OnStaAsync(async () =>
    {
        var dispatcher = Dispatcher.CurrentDispatcher;
        var signInUri = new Uri("https://auth.openai.com/authorize?state=synthetic-layout-test");
        var copied = new List<string>();
        using var setup = new CodexSetupViewModel(new ReadyProbe(), _ => Task.FromResult<string?>(null), (_, _) => Task.CompletedTask);
        using var model = new AccountConnectionViewModel(async (_, _, progress, token) =>
        {
            await progress(new EnrollmentProgress("authorization_required",
                new LoginPrompt("chatgptDeviceCode", "test-login", null, signInUri, "TEST-CODE")));
            await Task.Delay(Timeout.Infinite, token);
        }, 1, null, null, setup,
            _ => throw new InvalidOperationException("Synthetic missing browser handler"),
            copied.Add, action => dispatcher.InvokeAsync(action).Task);
        var window = new AccountConnectionWindow(model, Resources())
        {
            ShowActivated = false, ShowInTaskbar = false,
            WindowStartupLocation = WindowStartupLocation.Manual, Left = -32000, Top = -32000
        };
        Task? connecting = null;
        var result = await ShowAndDriveAsync(window, async () =>
        {
            await model.InitializeAsync();
            window.Width = 500;
            window.Height = 360;
            window.UpdateLayout();
            var border = Assert.IsType<Border>(window.Content);
            var layout = Assert.IsType<Grid>(border.Child);
            var scroll = Assert.Single(layout.Children.OfType<ScrollViewer>());
            Assert.Equal(ResizeMode.CanResizeWithGrip, window.ResizeMode);
            Assert.True(scroll.ViewportHeight > 0);
            Assert.True(scroll.ExtentHeight > scroll.ViewportHeight);
            foreach (var button in new[] { ButtonByText(window, "Connect account"), ButtonByText(window, "Cancel") })
                AssertWithin(button, layout);

            connecting = model.ConnectAsync();
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            window.UpdateLayout();
            Assert.Contains("Copy sign-in link", model.Status);
            var copy = ButtonByText(window, "Copy sign-in link");
            var reopen = ButtonByText(window, "Reopen browser");
            Assert.True(copy.IsVisible && copy.IsEnabled);
            Assert.True(reopen.IsVisible && reopen.IsEnabled);
            scroll.ScrollToBottom();
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            window.UpdateLayout();
            AssertWithin(copy, scroll);
            AssertWithin(reopen, scroll);
            AssertWithin(ButtonByText(window, "Cancel"), layout);
            copy.Command.Execute(null);
            Assert.Equal(signInUri.AbsoluteUri, Assert.Single(copied));
            ButtonByText(window, "Cancel").Command.Execute(null);
        });
        if (connecting is not null) await connecting;
        Assert.False(result);
        Assert.False(model.IsBusy);
    });

    private static void AssertWithin(FrameworkElement control, FrameworkElement container)
    {
        var bounds = control.TransformToAncestor(container).TransformBounds(new Rect(control.RenderSize));
        Assert.True(control.ActualHeight > 0 && control.ActualWidth > 0);
        Assert.True(bounds.Left >= 0 && bounds.Right <= container.ActualWidth + 1
            && bounds.Top >= 0 && bounds.Bottom <= container.ActualHeight + 1,
            $"{control.GetType().Name} must remain reachable within {container.GetType().Name} at compact size.");
    }

    private static ResourceDictionary Resources()
    {
        var resources = new ResourceDictionary();
        resources.MergedDictionaries.Add((ResourceDictionary)Application.LoadComponent(
            new Uri("/CodexAccountMonitor;component/Resources/Theme.xaml", UriKind.Relative)));
        resources["BoolToVisibility"] = new BooleanToVisibilityConverter();
        resources["InverseBoolean"] = new InverseBooleanConverter();
        return resources;
    }

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
        Exception? error = null;
        var timedOut = false;
        var watchdog = new DispatcherTimer { Interval = TimeSpan.FromSeconds(12) };
        watchdog.Tick += (_, _) => { timedOut = true; watchdog.Stop(); window.Close(); };
        window.Loaded += (_, _) => operation = DriveAsync();
        async Task DriveAsync()
        {
            try { await drive(); }
            catch (Exception exception) { error = exception; window.Close(); }
        }
        watchdog.Start();
        try
        {
            var result = window.ShowDialog();
            if (operation is not null) await operation.WaitAsync(TimeSpan.FromSeconds(3));
            Assert.False(timedOut, "Synthetic connection dialog exceeded its bounded timeout.");
            if (error is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(error).Throw();
            return result;
        }
        finally { watchdog.Stop(); if (window.IsVisible) window.Close(); }
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
        }) { IsBackground = true, Name = "Synthetic account connection dialog regression" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return completion.Task.WaitAsync(TimeSpan.FromSeconds(20));
    }

    private sealed class ReadyProbe : ICodexInstallationProbe
    {
        public Task<CodexInstallationStatus> InspectAsync(string? configuredPath, bool testAppServer, CancellationToken cancellationToken = default) =>
            Task.FromResult(new CodexInstallationStatus(true, "synthetic.exe", "1.0.0", "Ready"));
    }
}

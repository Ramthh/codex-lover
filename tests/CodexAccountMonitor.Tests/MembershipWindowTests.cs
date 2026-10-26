using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Threading;
using CodexAccountMonitor.Infrastructure.Membership;

namespace CodexAccountMonitor.Tests;

/// <summary>Exercises the actual modal WPF window with synthetic membership and no browser or live server.</summary>
public sealed class MembershipWindowTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task RetryAfterTransientProviderFailureEnablesNewUserSignInWithoutRelaunch(bool timeout) => OnStaAsync(async () =>
    {
        using var fixture = new Fixture(active: true)
        {
            ConfigurationResponder = (request, _) => request == 1 && timeout
                ? Task.FromException<HttpResponseMessage>(new TaskCanceledException("Synthetic provider timeout."))
                : Task.FromResult(request == 1
                    ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable) : Fixture.ConfigurationResponse())
        };
        var opened = 0;
        var window = HiddenWindow(fixture.Service, _ => opened++);
        await ShowAndDriveAsync(window, async () =>
        {
            await UntilAsync(() => HasProviderLoadError(window));
            Assert.False(GoogleButton(window).IsEnabled);
            Assert.False(fixture.Service.HasSession);
            ButtonByText(window, "Retry membership check").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await UntilAsync(() => GoogleButton(window).IsEnabled);
            Assert.Equal(2, fixture.ConfigurationRequests);
            Assert.Equal("Sign in securely in your browser", GoogleButton(window).ToolTip);
            Assert.False(HasProviderLoadError(window));
            Assert.Equal(0, fixture.ValidationRequests);
            Assert.Equal(0, fixture.DeviceRequests);
            Assert.False(ButtonByText(window, "Continue to app").IsEnabled);

            GoogleButton(window).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await UntilAsync(() => opened == 1);
            Assert.Equal(1, fixture.DeviceRequests);
            ButtonByText(window, "Cancel sign-in").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await UntilAsync(() => GoogleButton(window).IsEnabled);
            Assert.False(fixture.Service.HasSession);
            window.Close();
        });
    });

    [Fact]
    public Task RepeatedProviderFailureKeepsActionableRetryMessageForNewUser() => OnStaAsync(async () =>
    {
        using var fixture = new Fixture(active: true)
        {
            ConfigurationResponder = (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable))
        };
        var window = HiddenWindow(fixture.Service, _ => throw new Exception("No browser should open."));
        await ShowAndDriveAsync(window, async () =>
        {
            await UntilAsync(() => HasProviderLoadError(window));
            var retry = ButtonByText(window, "Retry membership check");
            retry.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await UntilAsync(() => fixture.ConfigurationRequests == 2 && retry.IsEnabled);
            Assert.True(HasProviderLoadError(window));
            Assert.Contains(Descendants(window).OfType<TextBlock>(), text => text.Text.Contains("choose Retry membership check"));
            Assert.False(GoogleButton(window).IsEnabled);
            Assert.False(ButtonByText(window, "Continue to app").IsEnabled);
            Assert.Equal(0, fixture.DeviceRequests);
            window.Close();
        });
    });

    [Fact]
    public Task RetryRestoresProviderLabelWhenAvailabilityChanges() => OnStaAsync(async () =>
    {
        using var fixture = new Fixture(active: true)
        {
            ConfigurationResponder = (request, _) => Task.FromResult(Fixture.ConfigurationResponse(google: request > 1))
        };
        var window = HiddenWindow(fixture.Service, _ => throw new Exception("No browser should open."));
        await ShowAndDriveAsync(window, async () =>
        {
            await UntilAsync(() => GoogleLabel(window).Text == "Google sign-in · unavailable");
            ButtonByText(window, "Retry membership check").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await UntilAsync(() => GoogleButton(window).IsEnabled);
            Assert.Equal("Continue with Google", GoogleLabel(window).Text);
            Assert.Equal("Sign in securely in your browser", GoogleButton(window).ToolTip);
            Assert.Equal(2, fixture.ConfigurationRequests);
            window.Close();
        });
    });

    [Fact]
    public Task RetrySharesPendingInitialProviderRequestAndRejectsConcurrentClicks() => OnStaAsync(async () =>
    {
        var finish = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var fixture = new Fixture(active: true) { ConfigurationResponder = (_, token) => finish.Task.WaitAsync(token) };
        var window = HiddenWindow(fixture.Service, _ => throw new Exception("No browser should open."));
        await ShowAndDriveAsync(window, async () =>
        {
            await UntilAsync(() => fixture.ConfigurationRequests == 1);
            var retry = ButtonByText(window, "Retry membership check");
            retry.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            retry.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.False(retry.IsEnabled);
            Assert.Equal(1, fixture.ConfigurationRequests);
            finish.SetResult(Fixture.ConfigurationResponse());
            await UntilAsync(() => GoogleButton(window).IsEnabled && retry.IsEnabled);
            Assert.Equal(1, fixture.ConfigurationRequests);
            Assert.Equal(0, fixture.DeviceRequests);
            Assert.False(fixture.Service.HasSession);
            window.Close();
        });
    });

    [Fact]
    public Task ClosingPendingProviderRetryCancelsRequestAndDoesNotContinueOnDisposedWindow() => OnStaAsync(async () =>
    {
        var cancelled = false;
        using var fixture = new Fixture(active: true)
        {
            ConfigurationResponder = async (request, token) =>
            {
                if (request == 1) return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
                try { await Task.Delay(Timeout.Infinite, token); }
                finally { cancelled = token.IsCancellationRequested; }
                return Fixture.ConfigurationResponse();
            }
        };
        var window = HiddenWindow(fixture.Service, _ => throw new Exception("No browser should open."));
        var result = await ShowAndDriveAsync(window, async () =>
        {
            await UntilAsync(() => HasProviderLoadError(window));
            var retry = ButtonByText(window, "Retry membership check");
            retry.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await UntilAsync(() => fixture.ConfigurationRequests == 2);
            window.Close();
            await UntilAsync(() => cancelled);
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            retry.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.Equal(2, fixture.ConfigurationRequests);
            Assert.False(window.IsVisible);
            Assert.False(fixture.Service.HasSession);
            Assert.Equal(0, fixture.ValidationRequests);
            Assert.Equal(0, fixture.DeviceRequests);
        });
        Assert.False(result);
    });

    [Fact]
    public Task ProviderFailureDoesNotBlockExistingSessionValidationRetry() => OnStaAsync(async () =>
    {
        using var fixture = new Fixture(active: true, persistedSession: true)
        {
            ConfigurationResponder = (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable))
        };
        var window = HiddenWindow(fixture.Service, _ => throw new Exception("No browser should open."));
        var result = await ShowAndDriveAsync(window, async () =>
        {
            await UntilAsync(() => HasProviderLoadError(window));
            ButtonByText(window, "Retry membership check").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        });
        Assert.True(result);
        Assert.True(fixture.Service.CanUse);
        Assert.Equal(2, fixture.ConfigurationRequests);
        Assert.Equal(1, fixture.ValidationRequests);
        Assert.Equal(0, fixture.DeviceRequests);
    });

    [Fact]
    public Task UnavailableXSignInIsDisabledAndMembershipContentCanScroll() => OnStaAsync(async () =>
    {
        using var fixture = new Fixture(active: true);
        var window = HiddenWindow(fixture.Service, _ => throw new Exception("No browser should open."));
        await ShowAndDriveAsync(window, async () =>
        {
            await UntilAsync(() => GoogleButton(window).IsEnabled);
            var x = Descendants(window).OfType<Button>().Single(b => AutomationProperties.GetName(b) == "Continue with X");
            Assert.False(x.IsEnabled);
            Assert.Contains("not available", x.ToolTip.ToString());
            Assert.IsType<ScrollViewer>(window.Content);
            window.Close();
        });
    });

    [Fact]
    public Task SuccessfulGoogleSignInCompletesDialogWithoutContinueClick() => OnStaAsync(async () =>
    {
        using var fixture = new Fixture(active: true);
        Uri? opened = null;
        var window = HiddenWindow(fixture.Service, uri => opened = uri);
        var result = await ShowAndDriveAsync(window, () =>
        {
            GoogleButton(window).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            return Task.CompletedTask;
        });

        Assert.True(result);
        Assert.True(fixture.Service.CanUse);
        Assert.Equal("/device", opened?.AbsolutePath);
        Assert.Equal(1, fixture.TokenRequests);
        Assert.False(window.IsVisible);
    });

    [Fact]
    public Task SignedInButInactiveMembershipKeepsDialogOpen() => OnStaAsync(async () =>
    {
        using var fixture = new Fixture(active: false);
        var window = HiddenWindow(fixture.Service, _ => { });
        var result = await ShowAndDriveAsync(window, async () =>
        {
            GoogleButton(window).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await UntilAsync(() => fixture.Service.HasSession);
            // Allow the SignInAsync UI continuation to finish before checking the dialog.
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            Assert.False(fixture.Service.CanUse);
            Assert.True(window.IsVisible);
            Assert.False(ButtonByText(window, "Continue to app").IsEnabled);
            window.Close();
        });

        Assert.False(result);
    });

    [Fact]
    public Task SuccessfulRetryCompletesDialogWithoutContinueClick() => OnStaAsync(async () =>
    {
        using var fixture = new Fixture(active: true, persistedSession: true);
        Assert.False(fixture.Service.CanUse);
        var window = HiddenWindow(fixture.Service, _ => throw new InvalidOperationException("Retry must not open a browser."));
        var result = await ShowAndDriveAsync(window, () =>
        {
            ButtonByText(window, "Retry membership check").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            return Task.CompletedTask;
        });

        Assert.True(result);
        Assert.True(fixture.Service.CanUse);
        Assert.Equal(1, fixture.ValidationRequests);
        Assert.Equal(0, fixture.TokenRequests);
    });

    [Fact]
    public Task ClosingDuringBrowserSignInCancelsWithoutReopening() => OnStaAsync(async () =>
    {
        using var fixture = new Fixture(active: true);
        var browserOpened = false;
        var window = HiddenWindow(fixture.Service, _ => browserOpened = true);
        var result = await ShowAndDriveAsync(window, async () =>
        {
            GoogleButton(window).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await UntilAsync(() => browserOpened);
            window.Close();
            await Task.Delay(150);
            Assert.False(window.IsVisible);
            Assert.False(fixture.Service.HasSession);
        });

        Assert.False(result);
        Assert.Equal(0, fixture.TokenRequests);
    });

    [Fact]
    public Task OpeningExistingActiveMembershipDoesNotCloseManagementDialog() => OnStaAsync(async () =>
    {
        using var fixture = new Fixture(active: true, persistedSession: true);
        Assert.True(await fixture.Service.ValidateAsync());
        var window = HiddenWindow(fixture.Service, _ => { });
        var result = await ShowAndDriveAsync(window, async () =>
        {
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            Assert.True(window.IsVisible);
            Assert.True(ButtonByText(window, "Continue to app").IsEnabled);
            window.Close();
        });

        Assert.False(result);
    });

    [Fact]
    public Task PendingApprovalCanReopenSameRequestAndOnlyClosesAfterApproval() => OnStaAsync(async () =>
    {
        using var fixture = new Fixture(active: true) { TokenStatus = HttpStatusCode.Accepted };
        var opened = new List<Uri>();
        var window = HiddenWindow(fixture.Service, opened.Add);
        var result = await ShowAndDriveAsync(window, async () =>
        {
            GoogleButton(window).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await UntilAsync(() => opened.Count == 1);
            await UntilAsync(() => fixture.TokenRequests > 0);
            Assert.False(fixture.Service.HasSession);
            Assert.True(window.IsVisible);
            Assert.Contains(Descendants(window).OfType<TextBlock>(), text => text.Text.Contains("Connect this app"));
            Assert.False(ButtonByText(window, "Retry membership check").IsEnabled);
            Assert.False(ButtonByText(window, "Manage membership / Get Pro ↗").IsEnabled);
            ButtonByText(window, "Reopen sign-in page").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.Equal(2, opened.Count);
            Assert.Equal(opened[0], opened[1]);
            Assert.Equal(1, fixture.DeviceRequests);
            fixture.TokenStatus = HttpStatusCode.OK;
        });
        Assert.True(result);
        Assert.True(fixture.Service.CanUse);
    });

    [Fact]
    public Task BrowserLaunchFailureCanRetryAndCancellationStartsFreshRequest() => OnStaAsync(async () =>
    {
        using var fixture = new Fixture(active: true) { TokenStatus = HttpStatusCode.Accepted };
        var opened = new List<Uri>();
        var window = HiddenWindow(fixture.Service, uri =>
        {
            opened.Add(uri);
            if (opened.Count == 1) throw new InvalidOperationException("Synthetic missing browser.");
        });
        await ShowAndDriveAsync(window, async () =>
        {
            GoogleButton(window).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await UntilAsync(() => opened.Count == 1);
            Assert.Contains(Descendants(window).OfType<TextBlock>(), text => text.Text.Contains("Could not open your browser"));
            var reopen = ButtonByText(window, "Reopen sign-in page");
            Assert.Equal(Visibility.Visible, ((StackPanel)reopen.Parent).Visibility);
            reopen.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.Equal(opened[0], opened[1]);
            ButtonByText(window, "Cancel sign-in").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await UntilAsync(() => GoogleButton(window).IsEnabled);
            Assert.Equal(Visibility.Collapsed, ((StackPanel)reopen.Parent).Visibility);
            reopen.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.Equal(2, opened.Count);
            Assert.False(fixture.Service.HasSession);
            GoogleButton(window).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await UntilAsync(() => opened.Count == 3);
            Assert.NotEqual(opened[0], opened[2]);
            Assert.Equal(2, fixture.DeviceRequests);
            ButtonByText(window, "Cancel sign-in").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await UntilAsync(() => GoogleButton(window).IsEnabled);
            window.Close();
        });
        Assert.False(fixture.Service.HasSession);
    });

    [Fact]
    public Task ExpiredRequestRemovesReopenLinkAndAllowsNewSignIn() => OnStaAsync(async () =>
    {
        using var fixture = new Fixture(active: true) { TokenStatus = HttpStatusCode.Gone };
        var opened = new List<Uri>();
        var window = HiddenWindow(fixture.Service, opened.Add);
        await ShowAndDriveAsync(window, async () =>
        {
            GoogleButton(window).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await UntilAsync(() => fixture.TokenRequests > 0 && GoogleButton(window).IsEnabled);
            var reopen = ButtonByText(window, "Reopen sign-in page");
            Assert.Equal(Visibility.Collapsed, ((StackPanel)reopen.Parent).Visibility);
            Assert.Contains(Descendants(window).OfType<TextBlock>(), text => text.Text.Contains("expired"));
            reopen.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.Single(opened);
            Assert.False(fixture.Service.HasSession);
            Assert.True(ButtonByText(window, "Retry membership check").IsEnabled);
            window.Close();
        });
    });

    private static MembershipWindow HiddenWindow(MembershipService service, Action<Uri> openBrowser) =>
        new(service, openBrowser)
        {
            ShowActivated = false,
            ShowInTaskbar = false,
            WindowStartupLocation = WindowStartupLocation.Manual,
            Left = -32000,
            Top = -32000
        };

    private static Button GoogleButton(Window window) => Descendants(window).OfType<Button>().Single(button =>
        AutomationProperties.GetName(button) == "Continue with Google");

    private static TextBlock GoogleLabel(Window window) => Assert.IsType<StackPanel>(GoogleButton(window).Content)
        .Children.OfType<TextBlock>().Last();
    private static bool HasProviderLoadError(Window window) => Descendants(window).OfType<TextBlock>()
        .Any(text => text.Text.Contains("Could not load sign-in options"));

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
            Assert.False(timedOut, "The membership dialog did not complete within the bounded test timeout.");
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
            Assert.True(DateTime.UtcNow < timeout, "The expected membership transition did not occur.");
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
        }) { IsBackground = true, Name = "Membership modal regression test" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return completion.Task.WaitAsync(TimeSpan.FromSeconds(20));
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "CodexLover-window-tests", Guid.NewGuid().ToString("N"));
        private readonly DateTimeOffset _now = DateTimeOffset.UtcNow;
        private readonly MembershipEntitlement _entitlement;
        public MembershipService Service { get; }
        public int TokenRequests { get; private set; }
        public int ValidationRequests { get; private set; }
        public int DeviceRequests { get; private set; }
        public int ConfigurationRequests { get; private set; }
        public Func<int, CancellationToken, Task<HttpResponseMessage>>? ConfigurationResponder { get; set; }
        public HttpStatusCode TokenStatus { get; set; } = HttpStatusCode.OK;

        public Fixture(bool active, bool persistedSession = false)
        {
            _entitlement = new("trial", active, 2, active ? _now.AddDays(3) : _now.AddDays(-1), null);
            var store = new MembershipSessionStore(Path.Combine(_root, "session.dpapi"));
            if (persistedSession) store.Save(new("synthetic-window-session", _now.AddDays(7)));
            Service = new MembershipService(store, new HttpClient(new Handler(RespondAsync)));
        }

        public static HttpResponseMessage ConfigurationResponse(bool google = true) =>
            Json(new SignInConfiguration(new() { ["google"] = google, ["twitter"] = false }));

        private Task<HttpResponseMessage> RespondAsync(HttpRequestMessage request, CancellationToken token)
        {
            Assert.Equal("codexlover.com", request.RequestUri?.Host);
            if (request.RequestUri?.AbsolutePath == "/api/public/config")
            {
                ConfigurationRequests++;
                return ConfigurationResponder?.Invoke(ConfigurationRequests, token)
                    ?? Task.FromResult(ConfigurationResponse());
            }
            return Task.FromResult(Respond(request));
        }

        private HttpResponseMessage Respond(HttpRequestMessage request)
        {
            Assert.Equal("codexlover.com", request.RequestUri?.Host);
            switch (request.RequestUri?.AbsolutePath)
            {
                case "/api/device/start":
                    DeviceRequests++;
                    return Json(new DeviceAuthorization($"synthetic-device-{DeviceRequests}", $"https://codexlover.com/device?code=synthetic-{DeviceRequests}", 60, 3));
                case "/api/device/token":
                    TokenRequests++;
                    if (TokenStatus != HttpStatusCode.OK) return new HttpResponseMessage(TokenStatus);
                    return Json(new DeviceTokenResponse("synthetic-window-session", _now.AddDays(7),
                        new("synthetic-member", "Test Member", null), _entitlement));
                case "/api/me":
                    ValidationRequests++;
                    return Json(new MembershipResponse(new("synthetic-member", "Test Member", null), _entitlement));
                default:
                    throw new InvalidOperationException("Unexpected mocked membership request.");
            }
        }

        private static HttpResponseMessage Json<T>(T value) => new(HttpStatusCode.OK)
        {
            Content = new StringContent(JsonSerializer.Serialize(value, new JsonSerializerOptions(JsonSerializerDefaults.Web)),
                Encoding.UTF8, "application/json")
        };

        public void Dispose()
        {
            Service.Dispose();
            if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        }
    }

    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            respond(request, cancellationToken);
    }
}

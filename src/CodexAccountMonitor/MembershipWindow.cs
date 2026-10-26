using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using CodexAccountMonitor.Infrastructure.Membership;

namespace CodexAccountMonitor;

/// <summary>Website membership is separate from locally stored Codex sign-ins.</summary>
public sealed class MembershipWindow : Window
{
    private readonly MembershipService _membership;
    private readonly TextBlock _status;
    private readonly StackPanel _actions;
    private readonly Button _continue;
    private readonly Button _signout;
    private readonly Button _retry;
    private readonly Button _billing;
    private readonly StackPanel _browserActions;
    private readonly Action<Uri> _openBrowser;
    private Uri? _pendingBrowserUri;
    private const string BrowserInstructions = "On the website, finish sign-in, then choose Connect this app. This window will continue automatically after approval. If you reached your dashboard, choose Reopen sign-in page to return to the connection request.";
    private CancellationTokenSource? _operation;
    private bool _closed;
    private readonly Dictionary<string, Button> _providers = new();
    private readonly CancellationTokenSource _providerLifetime = new();
    private Task<bool>? _providerLoad;
    public MembershipWindow(MembershipService membership, Action<Uri>? openBrowser = null)
    {
        _membership = membership;
        _openBrowser = openBrowser ?? MembershipService.OpenWebsite;
        Title = "Codex Lover · Membership";
        Width = 480; Height = Math.Min(650, SystemParameters.WorkArea.Height - 30);
        MinWidth = 360; MinHeight = 340; ResizeMode = ResizeMode.CanResizeWithGrip;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Background = new SolidColorBrush(Color.FromRgb(13, 21, 28));
        Foreground = Brushes.White;
        var panel = new StackPanel { Margin = new Thickness(30) };
        panel.Children.Add(new TextBlock { Text = "Welcome to Codex Lover", FontSize = 25, FontWeight = FontWeights.SemiBold });
        panel.Children.Add(new TextBlock { Text = "Sign in to use two Codex accounts free, with no time limit. Monthly Pro unlocks unlimited saved accounts for $9.99 USD per month.",
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 12, 0, 14), Foreground = Brushes.LightGray });
        panel.Children.Add(new TextBlock { Text = "This signs you into Codex Lover membership only. Next, choose Add account to connect your Codex account separately. Your Codex credentials stay on this PC.",
            TextWrapping = TextWrapping.Wrap, FontSize = 12, Foreground = Brushes.LightGray, Margin = new Thickness(0, 0, 0, 12) });
        _actions = new StackPanel();
        _actions.Children.Add(ProviderButton("G", "Continue with Google", "google"));
        _actions.Children.Add(ProviderButton("𝕏", "Continue with X", "twitter"));
        panel.Children.Add(_actions);
        _status = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 15, 0, 10), MinHeight = 55,
            Foreground = Brushes.LightGreen };
        panel.Children.Add(_status);
        _browserActions = new StackPanel { Visibility = Visibility.Collapsed };
        var reopen = new Button { Content = "Reopen sign-in page", Margin = new Thickness(0, 3, 0, 3), Padding = new Thickness(8) };
        reopen.Click += (_, _) => ReopenSignInPage();
        _browserActions.Children.Add(reopen);
        var cancel = new Button { Content = "Cancel sign-in", Margin = new Thickness(0, 3, 0, 3), Padding = new Thickness(8) };
        cancel.Click += (_, _) => _operation?.Cancel();
        _browserActions.Children.Add(cancel);
        panel.Children.Add(_browserActions);
        _retry = new Button { Content = "Retry membership check", Margin = new Thickness(0, 3, 0, 3), Padding = new Thickness(8) };
        _retry.Click += async (_, _) => await RunAsync(async token =>
        {
            var providersLoaded = await LoadProvidersAsync();
            token.ThrowIfCancellationRequested();
            await _membership.ValidateAsync(token);
            // Preserve an actionable provider error for a new user with no session.
            // Existing members can still recover access if only config is unavailable.
            if (!_closed && (providersLoaded || _membership.CanUse)) UpdateStatus();
        }, continueWhenReady: true);
        panel.Children.Add(_retry);
        _billing = new Button { Content = "Manage membership / Get Pro ↗", Margin = new Thickness(0, 3, 0, 3), Padding = new Thickness(8) };
        _billing.Click += (_, _) => OpenLink("dashboard/billing");
        panel.Children.Add(_billing);
        var bottom = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 10, 0, 0) };
        _signout = new Button { Content = "Sign out", Padding = new Thickness(12, 6, 12, 6), Margin = new Thickness(0, 0, 8, 0) };
        _signout.Click += async (_, _) => await RunAsync(async token => { await _membership.SignOutAsync(token); UpdateStatus(); });
        bottom.Children.Add(_signout);
        _continue = new Button { Content = "Continue to app", Padding = new Thickness(12, 6, 12, 6) };
        _continue.Click += (_, _) => { if (_membership.CanUse) DialogResult = true; };
        bottom.Children.Add(_continue);
        panel.Children.Add(bottom);
        Content = new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        Loaded += async (_, _) => await LoadProvidersAsync();
        Closing += (_, _) => { _closed = true; _pendingBrowserUri = null; _operation?.Cancel(); _providerLifetime.Cancel(); };
        Closed += (_, _) => _providerLifetime.Dispose();
        UpdateStatus();
    }

    private Button ProviderButton(string icon, string text, string provider)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(new TextBlock { Text = icon, FontWeight = FontWeights.Bold, FontSize = 17, Width = 28 });
        row.Children.Add(new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center });
        var button = new Button { Content = row, Padding = new Thickness(12, 10, 12, 10), Margin = new Thickness(0, 3, 0, 3),
            HorizontalContentAlignment = HorizontalAlignment.Left };
        System.Windows.Automation.AutomationProperties.SetName(button, text);
        button.IsEnabled = false;
        button.ToolTip = "Checking sign-in availability…";
        _providers.Add(provider, button);
        button.Click += async (_, _) => await RunAsync(async token =>
        {
            _status.Text = "Preparing your browser sign-in…";
            await _membership.SignInAsync(provider, uri => Dispatcher.Invoke(() =>
            {
                if (_closed || token.IsCancellationRequested) return;
                _pendingBrowserUri = uri;
                _browserActions.Visibility = Visibility.Visible;
                ReopenSignInPage();
            }), token);
            UpdateStatus();
        }, continueWhenReady: true);
        return button;
    }

    private void ReopenSignInPage()
    {
        if (_closed || _pendingBrowserUri is null || _operation?.IsCancellationRequested != false) return;
        try
        {
            _openBrowser(_pendingBrowserUri);
            _status.Text = BrowserInstructions;
        }
        catch
        {
            _status.Text = "Could not open your browser. Keep this window open and choose Reopen sign-in page to try again. Then finish sign-in and choose Connect this app on the website.";
        }
    }

    private Task<bool> LoadProvidersAsync()
    {
        if (_closed) return Task.FromResult(false);
        // Retry can be clicked while the initial load is still pending. Share that
        // request so an older response cannot overwrite a newer availability result.
        if (_providerLoad is { IsCompleted: false }) return _providerLoad;
        return _providerLoad = LoadProvidersCoreAsync();
    }

    private async Task<bool> LoadProvidersCoreAsync()
    {
        try
        {
            var config = await _membership.GetSignInConfigurationAsync(_providerLifetime.Token);
            if (_closed) return false;
            foreach (var (provider, button) in _providers)
            {
                var available = config.Providers.GetValueOrDefault(provider);
                button.IsEnabled = available;
                button.ToolTip = available ? "Sign in securely in your browser" : "This sign-in option is not available yet. Please use another provider.";
                if (button.Content is StackPanel row && row.Children[1] is TextBlock label)
                    label.Text = available ? provider == "twitter" ? "Continue with X" : "Continue with Google"
                        : provider == "twitter" ? "X sign-in · unavailable" : "Google sign-in · unavailable";
            }
            return true;
        }
        catch (OperationCanceledException) when (_providerLifetime.IsCancellationRequested) { return false; }
        catch
        {
            if (!_closed) _status.Text = "Could not load sign-in options. Check your connection and choose Retry membership check. Existing verified access is preserved.";
            return false;
        }
    }

    private async Task RunAsync(Func<CancellationToken, Task> work, bool continueWhenReady = false)
    {
        if (_closed || _operation is not null) return;
        using var cancellation = new CancellationTokenSource();
        _operation = cancellation;
        _actions.IsEnabled = false; _signout.IsEnabled = false; _continue.IsEnabled = false;
        _retry.IsEnabled = false; _billing.IsEnabled = false;
        try
        {
            await work(cancellation.Token);
            if (continueWhenReady && !_closed && !cancellation.IsCancellationRequested && _membership.CanUse)
                DialogResult = true;
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        { if (!_closed) _status.Text = "Sign-in cancelled. Choose a provider to start again with a new browser link."; }
        catch (MembershipRequiredException exception) { _status.Text = exception.Message; }
        catch { _status.Text = "Codex Lover could not be reached. Check your connection and retry. Your local Codex accounts are preserved."; }
        finally
        {
            _operation = null;
            _pendingBrowserUri = null;
            if (!_closed)
            {
                _browserActions.Visibility = Visibility.Collapsed;
                _actions.IsEnabled = true;
                _retry.IsEnabled = true; _billing.IsEnabled = true;
                _continue.IsEnabled = _membership.CanUse;
                _signout.IsEnabled = _membership.HasSession;
            }
        }
    }

    private void UpdateStatus()
    {
        var membership = _membership.Current;
        _status.Text = _membership.CanUse
            ? membership!.Entitlement.Plan == "pro"
                ? $"{membership.User.Name ?? "Signed in"} · Pro until {membership.Entitlement.ProExpiresAt:MMM d, yyyy}."
                : $"{membership!.User.Name ?? "Signed in"} · Free plan: 2 accounts, no expiry."
            : _membership.HasSession ? "Your membership could not be verified. Check your connection, then choose Retry membership check."
            : "Choose a provider to sign in securely in your browser.";
        if (_membership.UsingCachedAccess) _status.Text += " Temporarily offline · using your recently verified membership for up to one hour.";
        _continue.IsEnabled = _membership.CanUse;
        _signout.IsEnabled = _membership.HasSession;
    }

    private void OpenLink(string path)
    {
        try { MembershipService.OpenWebsite(new Uri(MembershipService.Website, path)); }
        catch { _status.Text = "Open https://codexlover.com/dashboard/billing in your browser to manage membership."; }
    }
}

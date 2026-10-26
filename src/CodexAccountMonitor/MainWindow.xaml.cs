using System.ComponentModel;
using System.Collections.Specialized;
using System.Windows;
using System.Windows.Input;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using CodexAccountMonitor.Core.Models;
using CodexAccountMonitor.Infrastructure;
using CodexAccountMonitor.Infrastructure.Membership;

namespace CodexAccountMonitor;

public partial class MainWindow : Window
{
    private const double CompactWidth = 588;
    private const double ExpandedWidth = 1080;
    private const double NonAccountContentHeight = 305;
    private bool _allowClose;
    private bool _clickThrough;
    private bool _expandedLayout;
    private bool _initialPlacementApplied;
    private ViewModels.DashboardViewModel? _subscribedDashboard;
    private AppSettings _settings = new();
    private readonly Dictionary<string, Brush> _shellDefaults = new();
    private readonly DispatcherTimer _placementTimer = new() { Interval = TimeSpan.FromMilliseconds(600) };

    public MainWindow()
    {
        InitializeComponent();
        foreach (var key in new[] { "WidgetShellBrush", "WidgetCardBrush", "WidgetCardHoverBrush" })
            if (Resources[key] is Brush brush) _shellDefaults[key] = brush.Clone();
        SourceInitialized += (_, _) =>
        {
            if (Application.Current is not App { IsDemo: true })
                WindowNativeServices.ApplyWidgetWindowStyle(this, _settings.MinimizeToTray);
            WindowNativeServices.SetClickThrough(this, _clickThrough);
        };
        Loaded += OnLoaded;
        Closing += OnClosing;
        Closed += (_, _) => { _placementTimer.Stop(); UnsubscribeFromAccounts(); };
        ContentRendered += (_, _) => RefitToCurrentMonitor();
        DpiChanged += (_, _) => Dispatcher.BeginInvoke(new Action(RefitToCurrentMonitor));
        _placementTimer.Tick += async (_, _) => { _placementTimer.Stop(); await SavePlacementAsync(); };
        LocationChanged += (_, _) => SchedulePlacementSave();
        SizeChanged += (_, _) => SchedulePlacementSave();
    }

    public void ApplyWindowSettings(AppSettings settings)
    {
        _settings = settings;
        foreach (var pair in _shellDefaults) Resources[pair.Key] = pair.Value.Clone();
        ApplyWindowSettings(settings.AlwaysOnTop, settings.ClickThrough,
            settings.OpaqueBackground || SystemParameters.HighContrast ? 100 : settings.WidgetOpacityPercent);
        if (Application.Current is App { IsDemo: true }) ShowInTaskbar = true;
        else
        {
            ShowInTaskbar = !settings.MinimizeToTray;
            if (IsLoaded) WindowNativeServices.ApplyWidgetWindowStyle(this, settings.MinimizeToTray);
        }
        if (SystemParameters.HighContrast)
        {
            Resources["WidgetShellBrush"] = SystemColors.WindowBrush;
            Resources["WidgetCardBrush"] = SystemColors.WindowBrush;
            Resources["WidgetCardHoverBrush"] = SystemColors.ControlBrush;
        }
        if (IsLoaded) { UpdateLayout(); FitManualHeightToAccounts(); }
    }

    public void RefreshDesktopSettings() { ApplyWindowSettings(_settings); RefitToCurrentMonitor(); }

    public Task SavePlacementAsync() => Application.Current is App app && _initialPlacementApplied
        ? app.SaveWindowPlacementAsync(Left, Top, Width, _expandedLayout) : Task.CompletedTask;

    private void SchedulePlacementSave()
    {
        if (!_initialPlacementApplied || !IsLoaded) return;
        _placementTimer.Stop();
        _placementTimer.Start();
    }

    private void RefitToCurrentMonitor()
    {
        if (!IsLoaded) return;
        WindowNativeServices.UpdateMonitorBounds(this);
        AccountsScroller.MaxHeight = Math.Max(110, MaxHeight - NonAccountContentHeight);
        AccountItems.ApplyTemplate();
        AccountItems.InvalidateMeasure();
        FitManualHeightToAccounts();
        WindowNativeServices.KeepDesktopWidgetOnScreen(this);
    }

    public void ApplyWindowSettings(bool alwaysOnTop, bool clickThrough, int widgetOpacityPercent)
    {
        Topmost = alwaysOnTop;
        ApplyWidgetOpacity(widgetOpacityPercent);
        _clickThrough = clickThrough;
        if (IsLoaded)
        {
            WindowNativeServices.SetClickThrough(this, clickThrough);
        }
    }

    public void ShowAndActivate()
    {
        Show();
        WindowState = WindowState.Normal;
        Topmost = ((App)Application.Current).CurrentSettings.AlwaysOnTop;
        Activate();
    }

    public void AllowClose() => _allowClose = true;

    private void ApplyWidgetOpacity(int percentage)
    {
        var opacity = Math.Clamp(percentage, 35, 100) / 100d;
        SetBrushOpacity("WidgetShellBrush", opacity);
        SetBrushOpacity("WidgetCardBrush", opacity);
        SetBrushOpacity("WidgetCardHoverBrush", opacity);
    }

    private void SetBrushOpacity(string resourceKey, double opacity)
    {
        if (Resources[resourceKey] is not Brush source)
        {
            return;
        }

        var adjusted = source.Clone();
        adjusted.Opacity = opacity;
        Resources[resourceKey] = adjusted;
    }

    private void DragRegion_OnMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed && !IsInsideButton(e.OriginalSource as DependencyObject))
        {
            DragMove();
        }
    }

    private static bool IsInsideButton(DependencyObject? node)
    {
        while (node is not null)
        {
            if (node is Button) return true;
            node = node is Visual ? VisualTreeHelper.GetParent(node) : LogicalTreeHelper.GetParent(node);
        }
        return false;
    }

    private void MinimizeButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (_settings.MinimizeToTray) Hide();
        else { ShowInTaskbar = true; WindowState = WindowState.Minimized; }
    }
    private void ModelButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (Application.Current is App app) app.ToggleSolModel();
    }

    private void MembershipButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (Application.Current is App app) app.ShowMembership();
    }
    private void WebsiteButton_OnClick(object sender, RoutedEventArgs e)
    {
        try { MembershipService.OpenWebsite(MembershipService.Website); }
        catch { ShowModalMessage("Could not open your browser. Visit https://codexlover.com.", "Codex Lover website"); }
    }
    public void SetNews(IReadOnlyList<NewsArticle> articles) => NewsBar.SetArticles(articles);
    private Uri? _updateUri;
    public void SetAppUpdates(AppUpdates feed)
    {
        ResetsBar.SetDays(feed);
        if (StoreDistribution.IsStorePackage)
        {
            _updateUri = null;
            UpdateButton.Visibility = Visibility.Visible;
            UpdateButton.ToolTip = "Check for Codex Lover updates in Microsoft Store.";
            return;
        }
        var version = typeof(MainWindow).Assembly.GetName().Version ?? new Version(1, 6, 0);
        var available = AppUpdatePolicy.IsNewer(feed.LatestRelease, version);
        _updateUri = available ? new Uri(feed.LatestRelease!.Url) : null;
        UpdateButton.Visibility = available ? Visibility.Visible : Visibility.Collapsed;
        UpdateButton.ToolTip = available ? $"Version {feed.LatestRelease!.Version} is available. {feed.LatestRelease.Notes}" : null;
    }
    private void UpdateButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (StoreDistribution.IsStorePackage)
        {
            try { StoreDistribution.OpenUpdates(); }
            catch { ShowModalMessage("Open Microsoft Store and check your Library for Codex Lover updates.", "App update"); }
            return;
        }
        if (_updateUri is null) return;
        try { MembershipService.OpenWebsite(_updateUri); }
        catch { ShowModalMessage("Open codexlover.com in your browser to check the latest release.", "App update"); }
    }

    private void ShowModalMessage(string message, string title)
    {
        using var modelSuppression = (Application.Current as App)?.SuspendSolModelForDialog();
        MessageBox.Show(this, message, title);
    }
    private void FeedbackButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (Application.Current is App app) app.ShowFeedback();
    }
    public void SetMembershipState(bool active, string label)
    {
        AccountItems.IsEnabled = active;
        MembershipButton.Content = label;
        MembershipButton.ToolTip = active ? "Manage your Codex Lover membership" : "Sign in or renew to use your saved Codex accounts";
    }
    private void CloseButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (_settings.MinimizeToTray) Hide();
        else if (Application.Current is App app) app.RequestExit();
    }

    private void LayoutButton_OnClick(object sender, RoutedEventArgs e)
    {
        var anchoredRight = Left + ActualWidth;
        _expandedLayout = !_expandedLayout;
        SizeToContent = SizeToContent.Manual;
        Width = _expandedLayout ? Math.Min(ExpandedWidth, MaxWidth) : CompactWidth;
        Left = anchoredRight - Width;
        FitManualHeightToAccounts();
        UpdateLayout();
        WindowNativeServices.KeepDesktopWidgetOnScreen(this);
        SchedulePlacementSave();
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (!_initialPlacementApplied)
        {
            WindowNativeServices.PlaceDesktopWidget(this);
            _expandedLayout = _settings.ExpandedLayout;
            Width = Math.Clamp(_settings.WindowWidth ?? (_expandedLayout ? ExpandedWidth : CompactWidth), MinWidth, MaxWidth);
            if (_settings.WindowLeft is double left && double.IsFinite(left)) Left = left;
            if (_settings.WindowTop is double top && double.IsFinite(top)) Top = top;
            SizeToContent = SizeToContent.Manual;
            FitManualHeightToAccounts();
            WindowNativeServices.KeepDesktopWidgetOnScreen(this);
            AccountsScroller.MaxHeight = Math.Max(110, MaxHeight - NonAccountContentHeight);
            _initialPlacementApplied = true;
        }

        SubscribeToAccounts();
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(RefitToCurrentMonitor));
    }

    private void SubscribeToAccounts()
    {
        if (ReferenceEquals(_subscribedDashboard, DataContext))
        {
            return;
        }

        UnsubscribeFromAccounts();
        if (DataContext is ViewModels.DashboardViewModel dashboard)
        {
            _subscribedDashboard = dashboard;
            dashboard.AllAccounts.CollectionChanged += OnAccountsCollectionChanged;
        }
    }

    private void UnsubscribeFromAccounts()
    {
        if (_subscribedDashboard is not null)
        {
            _subscribedDashboard.AllAccounts.CollectionChanged -= OnAccountsCollectionChanged;
            _subscribedDashboard = null;
        }
    }

    private void OnAccountsCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (SizeToContent != SizeToContent.Manual)
        {
            return;
        }

        _ = Dispatcher.BeginInvoke(new Action(() =>
        {
            FitManualHeightToAccounts();
            WindowNativeServices.KeepDesktopWidgetOnScreen(this);
        }));
    }

    private void FitManualHeightToAccounts()
    {
        AccountItems.Measure(new Size(Math.Max(0, Width - 31), double.PositiveInfinity));
        Height = Math.Min(MaxHeight, AccountItems.DesiredSize.Height + NonAccountContentHeight);
    }

    private void AccountMenuButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button button && button.ContextMenu is not null)
        {
            button.ContextMenu.PlacementTarget = button;
            button.ContextMenu.IsOpen = true;
        }
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (!_allowClose)
        {
            e.Cancel = true;
            if (_settings.MinimizeToTray) Hide();
            else Dispatcher.BeginInvoke(new Action(() => (Application.Current as App)?.RequestExit()));
        }
    }
}

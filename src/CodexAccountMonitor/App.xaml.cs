using System.Collections.Concurrent;
using System.Windows;
using CodexAccountMonitor.Core.Models;
using CodexAccountMonitor.Core.Services;
using CodexAccountMonitor.Infrastructure;
using CodexAccountMonitor.Infrastructure.AppServer;
using CodexAccountMonitor.ViewModels;
using CodexAccountMonitor.Infrastructure.DesktopSwitching;
using CodexAccountMonitor.Infrastructure.Membership;

namespace CodexAccountMonitor;

public partial class App : Application
{
    private SingleInstanceCoordinator? _singleInstance;
    private SafeJsonLogger? _logger;
    private SettingsRepository? _settingsRepository;
    private AccountMonitorService? _monitor;
    private ProfileSwitchCoordinator? _switchCoordinator;
    private RefreshScheduler? _scheduler;
    private SystemTrayService? _tray;
    private DashboardViewModel? _dashboard;
    private MainWindow? _mainWindow;
    private MembershipService? _membership;
    private MembershipWindow? _membershipWindow;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly ConcurrentDictionary<Guid, CancellationTokenSource> _notificationRefreshes = new();
    private readonly ConcurrentDictionary<Guid, RecoveryWorkState> _recoveryWork = new();
    private readonly ManagedSessionRecoveryPolicy _recoveryPolicy = new();
    private readonly object _backgroundGate = new();
    private readonly HashSet<Task> _backgroundTasks = new();
    private readonly object _cleanupGate = new();
    private Task<bool>? _cleanupTask;
    private bool _cleanedUp;
    private bool _isDemo;
    private readonly QuotaAlertEvaluator _alerts = new();
    private int _showingUnhandledError;

    public AppSettings CurrentSettings { get; private set; } = new();
    public bool IsDemo => _isDemo;
    public bool IsLocalTest { get; private set; }

    public App()
    {
        DispatcherUnhandledException += (_, args) =>
        {
            _logger?.Error("ui_unhandled_exception", args.Exception);
            args.Handled = true;
            if (_isDemo)
            {
                File.WriteAllText(Path.Combine(Path.GetTempPath(), $"CodexAccountMonitor-demo-error-{Environment.ProcessId}.txt"), args.Exception.ToString());
                Shutdown(1);
                return;
            }
            if (!_lifetime.IsCancellationRequested
                && Interlocked.Exchange(ref _showingUnhandledError, 1) == 0)
            {
                try
                {
                    using var modelSuppression = SuspendSolModelForDialog();
                    MessageBox.Show(
                        _mainWindow,
                        "Codex Account Monitor encountered an unexpected error. No credential data was logged.",
                        "Unexpected error",
                        MessageBoxButton.OK,
                        MessageBoxImage.Error);
                }
                finally
                {
                    Interlocked.Exchange(ref _showingUnhandledError, 0);
                }
            }
        };
    }

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        try
        {
            await StartAsync(e).ConfigureAwait(true);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
            Shutdown();
        }
        catch (MonitorIndependentLaunchException exception)
        {
            using var modelSuppression = SuspendSolModelForDialog();
            MessageBox.Show(exception.Message, "Open Codex Lover safely", MessageBoxButton.OK, MessageBoxImage.Warning);
            Shutdown(1);
        }
        catch (AppDataStorageException exception)
        {
            using var modelSuppression = SuspendSolModelForDialog();
            MessageBox.Show(exception.Message, "Preserve Codex Lover data", MessageBoxButton.OK, MessageBoxImage.Warning);
            Shutdown(1);
        }
        catch (Exception exception)
        {
            _logger?.Error("application_start_failed", exception);
            using var modelSuppression = SuspendSolModelForDialog();
            MessageBox.Show(
                "Codex Account Monitor could not start. Check that its local data is readable, then try again.",
                "Startup failed",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            if (await CleanupAsync().ConfigureAwait(true))
            {
                Shutdown();
            }
        }
    }

    private async Task StartAsync(StartupEventArgs e)
    {
        IsLocalTest = e.Args.Contains("--local-test", StringComparer.OrdinalIgnoreCase);
        var diagnosticArgument = e.Args.FirstOrDefault(static value => value.StartsWith("--diagnostics=", StringComparison.Ordinal));
        if (diagnosticArgument is not null)
        {
            await WriteDiagnosticsAsync(diagnosticArgument[14..],
                e.Args.Contains("--verify-switcher-access", StringComparer.OrdinalIgnoreCase),
                e.Args.Contains("--verify-codex-runtime", StringComparer.OrdinalIgnoreCase));
            Shutdown();
            return;
        }
        _isDemo = e.Args.Any(static argument => string.Equals(argument, "--demo", StringComparison.OrdinalIgnoreCase));
        // Apply to direct launches and Explorer handoffs alike. Inherited API keys
        // are unrelated to the saved accounts and must not prevent the app opening.
        if (!_isDemo) MonitorStartupBootstrap.PrepareSavedAccountEnvironment();
        _singleInstance = new SingleInstanceCoordinator(_isDemo ? "Demo" : null);
        if (!_singleInstance.IsPrimary)
        {
            _singleInstance.SignalPrimary(e.Args.Contains("--settings", StringComparer.OrdinalIgnoreCase));
            _singleInstance.Dispose();
            Shutdown();
            return;
        }

        var independentLifetime = true;
        if (!_isDemo)
        {
            // Monitoring and connecting saved accounts do not require an Explorer
            // parent. Store activation and other launchers may assign legitimate jobs.
            // The desktop adapter checks lifetime safety before every automatic Quit.
            // Preserve data while holding the primary mutex regardless of launcher.
            AppDataStorageResolver.EnsureDefaultRoot();
            independentLifetime = new WindowsMonitorLifetimeGuard().IsIndependent();
            if (independentLifetime) MonitorStartupBootstrap.AcknowledgeIndependentLaunch(e.Args);
        }

        var minimized = e.Args.Any(static argument => string.Equals(argument, "--minimized", StringComparison.OrdinalIgnoreCase));
        var paths = _isDemo
            ? new AppDataPaths(Path.Combine(Path.GetTempPath(), "CodexAccountMonitor-Demo", Guid.NewGuid().ToString("N")))
            : new AppDataPaths();
        paths.EnsureBaseDirectories();
        _logger = new SafeJsonLogger(paths);
        if (!_isDemo && !independentLifetime)
            _logger.Info("monitor_launcher_requires_safe_desktop_quit", new Dictionary<string, object?>
            {
                ["independentLifetime"] = false
            });
        _settingsRepository = new SettingsRepository(paths);
        CurrentSettings = await _settingsRepository.LoadAsync().ConfigureAwait(true);
        ApplySystemColors();
        SystemParameters.StaticPropertyChanged += OnSystemParametersChanged;
        if (CurrentSettings.WidgetExperienceVersion < 3)
        {
            // Earlier builds defaulted to an application-sized, always-on-top
            // window. Upgrade once to the safer desktop-widget behavior; users
            // can still opt into topmost mode later from Settings.
            CurrentSettings = CurrentSettings with
            {
                AlwaysOnTop = false,
                WidgetOpacityPercent = Math.Clamp(CurrentSettings.WidgetOpacityPercent, 35, 95),
                WidgetExperienceVersion = 3
            };
            await _settingsRepository.SaveAsync(CurrentSettings).ConfigureAwait(true);
        }

        var repository = new AccountRepository(paths);
        var directories = new AccountDirectoryService(paths);
        var locator = new CodexExecutableLocator(_logger);
        var credentialStore = new CredentialManagerSecretStore();
        var startup = new StartupRegistrationService();
        try
        {
            // Preserve the user's existing startup choice while moving the Run
            // entry forward when a newer side-by-side build is launched.
            if (!_isDemo && !IsLocalTest && await startup.IsEnabledAsync())
            {
                await startup.SetEnabledAsync(true);
                if (!CurrentSettings.StartWithWindows)
                    CurrentSettings = await _settingsRepository.UpdateAsync(current => current with { StartWithWindows = true });
            }
        }
        catch (Exception exception)
        {
            _logger.Error("startup_registration_reconciliation_failed", exception);
        }

        var profileOperations = new ProfileOperationCoordinator();
        if (!_isDemo)
        {
            _membership = new MembershipService(new MembershipSessionStore(Path.Combine(paths.Root, "membership", "session.dpapi")));
            await _membership.ValidateAsync(_lifetime.Token);
            if (!_membership.CanUse)
            {
                using var modelSuppression = SuspendSolModelForDialog();
                _membershipWindow = new MembershipWindow(_membership);
                try
                {
                    if (_membershipWindow.ShowDialog() != true || !_membership.CanUse) { Shutdown(); return; }
                }
                finally { _membershipWindow = null; }
                // An explicit browser sign-in should return to the app even
                // when this process was originally started minimized.
                minimized = false;
            }
        }
        ManagedProfileSessionFactory? sessionFactory = null;
        if (!_isDemo)
        {
            sessionFactory = new ManagedProfileSessionFactory(
                locator,
                _settingsRepository,
                _logger,
                directories);
            _switchCoordinator = new ProfileSwitchCoordinator(sessionFactory, profileOperations);
            sessionFactory.RateLimitsUpdated += (_, profileId) => ScheduleNotificationRefresh(profileId);
            sessionFactory.TurnBecameIdle += (_, _) => RunBackground(
                ApplyPendingSwitchAsync,
                "pending_switch_failed");
            sessionFactory.SessionTerminated += (_, profileId) => ScheduleManagedSessionRecovery(profileId);
            _switchCoordinator.StatusChanged += (_, status) =>
            {
                if (status.Phase == ProfileSwitchPhase.Active
                    && status.ActiveProfileId is Guid active
                    && status.SafeErrorCode is null)
                {
                    _recoveryPolicy.Reset(active);
                }
            };

            try
            {
                var hasCredential = credentialStore.HasApiKey();
                if (CurrentSettings.ApiFallback.HasStoredCredential != hasCredential
                    || (CurrentSettings.ApiFallback.Enabled && !hasCredential))
                {
                    CurrentSettings = CurrentSettings with
                    {
                        ApiFallback = CurrentSettings.ApiFallback with
                        {
                            Enabled = CurrentSettings.ApiFallback.Enabled && hasCredential,
                            HasStoredCredential = hasCredential
                        }
                    };
                    await _settingsRepository.SaveAsync(CurrentSettings).ConfigureAwait(true);
                }
            }
            catch (Exception exception)
            {
                _logger.Error("credential_state_reconciliation_failed", exception);
            }
        }

        _monitor = new AccountMonitorService(
            repository,
            _settingsRepository,
            directories,
            paths,
            locator,
            _logger,
            profileOperations,
            _switchCoordinator);
        if (_membership is not null)
            _monitor.MembershipAccessGuard = (id, enrollment) => _membership.AssertAccess(
                _monitor.Accounts.Select(account => account.Profile.Id).ToArray(), id, enrollment);
        if (!_isDemo)
            _monitor.AccountsChanged += OnUsageChanged;

        DesktopAccountSwitchService? desktopSwitcher = null;
        if (!_isDemo)
        {
            try
            {
                var desktopHome = DesktopHomePolicy.DefaultHome;
                var desktopAuth = new DesktopAuthFileStore(desktopHome, Path.Combine(paths.Root, "desktop-switch"));
                var desktopRouter = new DesktopCredentialRouter(desktopAuth, paths, directories, locator, _settingsRepository, _logger);
                _monitor.ProfileClientFactory = desktopRouter.CreateClientAsync;
                desktopSwitcher = new DesktopAccountSwitchService(_monitor, paths, desktopHome, desktopAuth, desktopRouter,
                    new WindowsCodexDesktopAdapter(), profileOperations, locator, _settingsRepository, _logger);
            }
            catch (Exception exception) { _logger.Error("desktop_switching_unavailable", exception); }
        }

        IUserInteractionService interaction;
        if (_isDemo)
        {
            interaction = new DemoInteractionService(() => _mainWindow, SuspendSolModelForDialog);
        }
        else
        {
            interaction = new UserInteractionService(
                _monitor,
                _settingsRepository,
                credentialStore,
                startup,
                () => _mainWindow,
                desktopSwitcher,
                SuspendSolModelForDialog);
        }

        _dashboard = new DashboardViewModel(
            _isDemo ? null : _monitor,
            _isDemo ? null : _settingsRepository,
            _switchCoordinator,
            interaction,
            _isDemo,
            desktopSwitcher);
        _mainWindow = new MainWindow { DataContext = _dashboard };
        if (_membership is not null) _membership.Changed += OnMembershipChanged;
        MainWindow = _mainWindow;
        _mainWindow.ApplyWindowSettings(CurrentSettings);
        UpdateMembershipUi();

        _tray = new SystemTrayService(
            ShowMainWindow,
            () => _dashboard.RefreshAllCommand.Execute(null),
            ToggleClickThrough,
            interaction.ShowSettings,
            RequestExit,
            ToggleSolModel,
            ShowSolModelLimits);
        _tray.SetClickThroughChecked(CurrentSettings.ClickThrough);
        _singleInstance.StartActivationListener(Dispatcher, ShowMainWindow, interaction.ShowSettings, RequestExit);

        await _dashboard.InitializeAsync(_lifetime.Token).ConfigureAwait(true);
        InitializeSolModel();
        if (!string.IsNullOrWhiteSpace(_monitor.RecoveryMessage))
            _dashboard.ShowToast(_monitor.RecoveryMessage, severity: ToastSeverity.Warning);
        if (!minimized) ShowMainWindow();
        if (e.Args.Contains("--settings", StringComparer.OrdinalIgnoreCase)) interaction.ShowSettings();

        if (!_isDemo)
        {
            Microsoft.Win32.SystemEvents.PowerModeChanged += OnPowerModeChanged;
            System.Net.NetworkInformation.NetworkChange.NetworkAvailabilityChanged += OnNetworkAvailabilityChanged;
            _scheduler = new RefreshScheduler(_monitor, _settingsRepository, _logger);
            _scheduler.Start();
            RunBackground(InitialRefreshAsync, "initial_refresh_failed");
            RunBackground(MembershipAndNewsLoopAsync, "membership_refresh_failed");
        }

        _logger.Info("application_started", new Dictionary<string, object?> { ["demoMode"] = _isDemo });
    }

    public void ShowMembership()
    {
        if (_membership is null) { _dashboard?.ShowToast("Demo mode · sample profiles only. No membership or real Codex login is used."); return; }
        if (_membershipWindow is not null) { _membershipWindow.Activate(); return; }
        using var modelSuppression = SuspendSolModelForDialog();
        _membershipWindow = new MembershipWindow(_membership);
        try
        {
            if (_membershipWindow.ShowDialog() == true && _membership.CanUse)
                _mainWindow?.ShowAndActivate();
        }
        finally { _membershipWindow = null; UpdateMembershipUi(); }
    }

    private void OnMembershipChanged(object? sender, EventArgs args) => Dispatcher.BeginInvoke(new Action(UpdateMembershipUi));
    private void UpdateMembershipUi() => _mainWindow?.SetMembershipState(_membership?.CanUse ?? _isDemo,
        _membership?.CanUse == true ? _membership.Current!.Entitlement.Plan == "pro" ? _membership.UsingCachedAccess ? "Pro · Offline" : "Pro · Membership" : _membership.UsingCachedAccess ? "Free · Offline" : "Free · Membership"
            : "Sign in / Renew");

    public void ShowFeedback()
    {
        if (_membership is null) return;
        if (!_membership.HasSession) { ShowMembership(); return; }
        using var modelSuppression = SuspendSolModelForDialog();
        new FeedbackWindow(_membership) { Owner = _mainWindow }.ShowDialog();
    }

    private async Task MembershipAndNewsLoopAsync(CancellationToken token)
    {
        var iteration = 0;
        while (!token.IsCancellationRequested && _membership is not null)
        {
            if (iteration > 0) await _membership.ValidateAsync(token);
            UpdateMembershipUi();
            try { _mainWindow?.SetAppUpdates(await _membership.GetAppUpdatesAsync(token)); }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            catch { /* Preserve the last published calendar while the service is unavailable. */ }
            if (iteration % 5 == 0)
                try { _mainWindow?.SetNews(await _membership.GetNewsAsync(token)); }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
                catch { /* Keep last fetched news. Membership remains independent of public feed failures. */ }
            iteration++;
            await Task.Delay(TimeSpan.FromMinutes(2), token);
        }
    }

    public async Task ReloadAndApplySettingsAsync()
    {
        if (_settingsRepository is null)
        {
            return;
        }

        CurrentSettings = await _settingsRepository.LoadAsync().ConfigureAwait(true);
        _mainWindow?.ApplyWindowSettings(CurrentSettings);
        ApplySolModelSettings();
        _tray?.SetClickThroughChecked(CurrentSettings.ClickThrough);
        if (_dashboard is not null)
        {
            await _dashboard.ReloadSettingsAsync().ConfigureAwait(true);
            _dashboard.ShowToast("Settings saved");
        }
    }

    private async Task InitialRefreshAsync(CancellationToken cancellationToken)
    {
        await Task.Delay(700, cancellationToken).ConfigureAwait(true);
        if (_monitor?.Accounts.Count > 0)
        {
            await _monitor.RefreshAllAsync(cancellationToken).ConfigureAwait(true);
        }
    }

    public async Task SaveWindowPlacementAsync(double left, double top, double width, bool expanded)
    {
        if (_settingsRepository is null || _isDemo || _lifetime.IsCancellationRequested) return;
        try
        {
            CurrentSettings = await _settingsRepository.UpdateAsync(current => current with
            {
                WindowLeft = left, WindowTop = top, WindowWidth = width, ExpandedLayout = expanded
            }).ConfigureAwait(true);
        }
        catch (Exception exception) { _logger?.Error("window_placement_save_failed", exception); }
    }

    private static async Task WriteDiagnosticsAsync(string destination, bool verifySwitcherAccess = false, bool verifyCodexRuntime = false)
    {
        var paths = new AppDataPaths();
        // A diagnostic switcher probe must not provide an unlicensed alternate
        // path to usage reads. Pure compatibility metadata remains available.
        if (verifySwitcherAccess)
        {
            using var membership = new MembershipService(new MembershipSessionStore(Path.Combine(paths.Root, "membership", "session.dpapi")));
            verifySwitcherAccess = await membership.ValidateAsync();
        }
        var accounts = await new AccountRepository(paths).LoadAsync();
        var settings = await new SettingsRepository(paths).LoadAsync();
        var installation = await new CodexInstallationProbe().InspectAsync(settings.CodexExecutablePath, testAppServer: verifyCodexRuntime);
        var isolatedProtocolReady = verifyCodexRuntime && installation.IsReady
            && await VerifyIsolatedRuntimeAsync(installation.ExecutablePath!);
        using var run = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run");
        var report = new
        {
            version = typeof(App).Assembly.GetName().Version?.ToString(),
            executable = Environment.ProcessPath,
            dataRoot = paths.Root,
            accountCount = accounts.Count,
            healthyAccounts = accounts.Count(static account => account.Usage.Authentication == AuthenticationHealth.Healthy),
            pendingResetCount = accounts.Count(static account => account.Usage.HasPendingResetCredit),
            startWithWindows = await new StartupRegistrationService().IsEnabledAsync(),
            storePackage = StoreDistribution.IsStorePackage,
            packageFamily = StoreDistribution.ReadFamilyName(),
            independentLifetime = new WindowsMonitorLifetimeGuard().IsIndependent(),
            lifetime = WindowsMonitorLifetimeGuard.ReadDiagnostics(),
            startupCommand = run?.GetValue("CodexAccountMonitor") as string,
            savedStartupPreference = settings.StartWithWindows,
            codexInstallation = new { ready = installation.IsReady, supportChecked = verifyCodexRuntime, isolatedProtocolReady, executablePath = installation.ExecutablePath, version = installation.Version, message = installation.Message },
            switcher = ReadSwitcherDiagnostics(paths, accounts),
            switcherAccess = verifySwitcherAccess ? await SwitcherAccessProbe.VerifyAsync(paths) : null,
            checkedUtc = DateTimeOffset.UtcNow
        };
        var fullPath = Path.GetFullPath(destination);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        await File.WriteAllTextAsync(fullPath, System.Text.Json.JsonSerializer.Serialize(report,
            new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
    }

    private static async Task<bool> VerifyIsolatedRuntimeAsync(string executable)
    {
        // No membership bypass: this verifies only the protocol in a new, empty home.
        // It never starts login, reads a saved profile, or asks for usage information.
        var temporaryRoot = Path.Combine(Path.GetTempPath(), "CodexLover-runtime-check", Guid.NewGuid().ToString("N"));
        var home = Path.Combine(temporaryRoot, "codex-home");
        try
        {
            Directory.CreateDirectory(home);
            using var logger = new SafeJsonLogger(new AppDataPaths(temporaryRoot));
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            await using var client = await CodexAppServerClient.StartAsync(executable, home, logger, timeout.Token);
            return await client.ReadAccountAsync(refreshToken: false, timeout.Token) is null;
        }
        catch { return false; }
        finally
        {
            try { Directory.Delete(temporaryRoot, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private static object ReadSwitcherDiagnostics(AppDataPaths paths, IReadOnlyList<PersistedAccount> accounts)
    {
        try
        {
            var home = DesktopHomePolicy.DefaultHome;
            var state = new WindowsCodexDesktopAdapter().Inspect();
            var supportedConfiguration = true;
            try { DesktopHomePolicy.AssertSupported(home); } catch { supportedConfiguration = false; }
            var desktopAuthValid = false;
            try { desktopAuthValid = DesktopAuthFileStore.ReadSnapshotFromHome(home, false) is not null; } catch { }
            var validProfiles = 0;
            foreach (var account in accounts)
            {
                try
                {
                    var snapshot = DesktopAuthFileStore.ReadSnapshotFromHome(paths.CodexHome(account.Profile.Id), false);
                    if (snapshot?.Identity.EmailFingerprint == account.Profile.IdentityFingerprint && snapshot is not null)
                        validProfiles++;
                }
                catch { }
            }
            return new { supportedConfiguration, desktopAuthValid, validProfiles,
                desktopFound = state.Installation is not null, blockingProcessCount = state.BlockingProcesses.Count,
                canSwitchNow = state.CanSwitch && supportedConfiguration };
        }
        catch { return new { available = false }; }
    }

    private void ApplySystemColors()
    {
        var foregrounds = new[] { "PrimaryTextBrush", "SecondaryTextBrush", "MutedTextBrush", "MintBrush", "BlueBrush", "SuccessBrush", "WarningBrush", "DangerBrush" };
        var backgrounds = new[] { "ShellBrush", "CardBrush", "CardHoverBrush", "StatusPanelBrush", "InputBackgroundBrush", "ButtonBackgroundBrush", "ButtonHoverBrush", "ToastBackgroundBrush" };
        var popup = new[] { "PopupBackgroundBrush", "PopupBorderBrush", "PopupHoverBrush", "PopupSelectionBrush", "PopupHighlightTextBrush", "PopupDisabledTextBrush" };
        foreach (var key in foregrounds.Concat(backgrounds).Concat(popup)) Resources.Remove(key);
        if (!SystemParameters.HighContrast) return;
        foreach (var key in foregrounds) Resources[key] = SystemColors.WindowTextBrush;
        foreach (var key in backgrounds) Resources[key] = SystemColors.WindowBrush;
        Resources["PopupBackgroundBrush"] = SystemColors.WindowBrush;
        Resources["PopupBorderBrush"] = SystemColors.WindowTextBrush;
        Resources["PopupHoverBrush"] = SystemColors.HighlightBrush;
        Resources["PopupSelectionBrush"] = SystemColors.HighlightBrush;
        Resources["PopupHighlightTextBrush"] = SystemColors.HighlightTextBrush;
        Resources["PopupDisabledTextBrush"] = SystemColors.GrayTextBrush;
    }

    private void OnSystemParametersChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs args)
    {
        if (args.PropertyName is not ("HighContrast" or "WorkArea")) return;
        RunBackground(_ => { ApplySystemColors(); _mainWindow?.RefreshDesktopSettings(); return Task.CompletedTask; }, "display_settings_refresh_failed");
    }

    private void OnUsageChanged(object? sender, EventArgs args)
    {
        if (_isDemo || _lifetime.IsCancellationRequested) return;
        RunBackground(_ =>
        {
            if (_monitor is null) return Task.CompletedTask;
            var notifications = _monitor.Accounts.SelectMany(account => _alerts.Evaluate(
                account.Profile.Id, account.Profile.Alias, account.Usage, CurrentSettings, DateTimeOffset.Now)).ToArray();
            if (notifications.Length > 0)
                _tray?.ShowNotification(notifications.Length == 1 ? notifications[0].Title : "Account updates",
                    string.Join("\n", notifications.Select(static alert => alert.Message)));
            return Task.CompletedTask;
        }, "quota_alert_failed");
    }

    private void OnPowerModeChanged(object sender, Microsoft.Win32.PowerModeChangedEventArgs args)
    {
        if (args.Mode == Microsoft.Win32.PowerModes.Resume)
            RunBackground(InitialRefreshAsync, "resume_refresh_failed");
    }

    private void OnNetworkAvailabilityChanged(object? sender, System.Net.NetworkInformation.NetworkAvailabilityEventArgs args)
    {
        if (args.IsAvailable) RunBackground(InitialRefreshAsync, "network_recovery_refresh_failed");
    }

    private void ScheduleNotificationRefresh(Guid profileId)
    {
        if (_lifetime.IsCancellationRequested)
        {
            return;
        }

        var next = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        while (true)
        {
            if (_notificationRefreshes.TryGetValue(profileId, out var current))
            {
                if (!_notificationRefreshes.TryUpdate(profileId, next, current))
                {
                    continue;
                }

                try
                {
                    current.Cancel();
                }
                catch (ObjectDisposedException)
                {
                }
                break;
            }

            if (_notificationRefreshes.TryAdd(profileId, next))
            {
                break;
            }
        }

        RunBackground(
            async ignoredToken =>
            {
                try
                {
                    await Task.Delay(350, next.Token).ConfigureAwait(true);
                    if (_monitor is not null)
                    {
                        await _monitor.RefreshAsync(profileId, next.Token).ConfigureAwait(true);
                    }
                }
                finally
                {
                    if (_notificationRefreshes.TryGetValue(profileId, out var current)
                        && ReferenceEquals(current, next))
                    {
                        _notificationRefreshes.TryRemove(profileId, out _);
                    }

                    next.Dispose();
                }
            },
            "rate_limit_notification_refresh_failed");
    }

    private async Task ApplyPendingSwitchAsync(CancellationToken cancellationToken)
    {
        if (_switchCoordinator is null || _settingsRepository is null)
        {
            return;
        }

        var status = await _switchCoordinator.ApplyPendingIfIdleAsync(cancellationToken).ConfigureAwait(true);
        if (status.Phase == ProfileSwitchPhase.Active && status.ActiveProfileId is Guid active)
        {
            await _settingsRepository.UpdateAsync(settings => settings with { ActiveProfileId = active }, cancellationToken)
                .ConfigureAwait(true);
        }
    }

    private async Task RecoverManagedSessionAsync(Guid profileId, CancellationToken cancellationToken)
    {
        if (_switchCoordinator is null)
        {
            return;
        }

        var decision = _recoveryPolicy.Evaluate(profileId, DateTimeOffset.UtcNow);
        if (!decision.IsAllowed)
        {
            await _switchCoordinator.QuarantineActiveSessionAsync(
                    profileId,
                    "managed_session_recovery_circuit_open",
                    cancellationToken)
                .ConfigureAwait(true);
            if (_settingsRepository is not null)
            {
                await _settingsRepository.UpdateAsync(
                        settings => settings.ActiveProfileId == profileId
                            ? settings with { ActiveProfileId = null }
                            : settings,
                        cancellationToken)
                    .ConfigureAwait(true);
            }

            _logger?.Warning("managed_session_recovery_circuit_open", new Dictionary<string, object?>
            {
                ["profileId"] = profileId,
                ["attempt"] = decision.Attempt,
                ["retryAfterSeconds"] = decision.CircuitRemaining?.TotalSeconds
            });
            _dashboard?.ShowToast("Managed Codex session kept stopping, so automatic restart was paused");
            return;
        }

        if (decision.Delay > TimeSpan.Zero)
        {
            await Task.Delay(decision.Delay, cancellationToken).ConfigureAwait(true);
        }

        var status = await _switchCoordinator.RecoverTerminatedSessionAsync(profileId, cancellationToken)
            .ConfigureAwait(true);
        if (status.ActiveProfileId is Guid active
            && status.Phase is ProfileSwitchPhase.Active or ProfileSwitchPhase.RollbackSucceeded
            && _settingsRepository is not null)
        {
            try
            {
                await _settingsRepository.UpdateAsync(
                        settings => settings with { ActiveProfileId = active },
                        cancellationToken)
                    .ConfigureAwait(true);
            }
            catch (Exception exception)
            {
                _logger?.Error("recovered_profile_persistence_failed", exception);
            }
        }

        if (status.SafeErrorCode == "managed_session_restarted")
        {
            _dashboard?.ShowToast("Managed Codex session restarted after an unexpected exit");
        }
        else if (status.SafeErrorCode == "queued_switch_applied_after_termination")
        {
            _dashboard?.ShowToast("The queued profile became active after the previous session stopped");
        }
        else if (status.SafeErrorCode == "queued_switch_failed_after_termination")
        {
            _dashboard?.ShowToast("Queued switch failed; the previous profile was restarted");
        }
        else if (status.SafeErrorCode == "managed_session_restart_failed")
        {
            _dashboard?.ShowToast("Managed Codex session stopped unexpectedly and could not restart");
        }
    }

    private void ScheduleManagedSessionRecovery(Guid profileId)
    {
        if (_lifetime.IsCancellationRequested)
        {
            return;
        }

        var work = _recoveryWork.GetOrAdd(profileId, static _ => new RecoveryWorkState());
        lock (work.Gate)
        {
            work.Requested = true;
            if (work.Running)
            {
                return;
            }

            work.Running = true;
        }

        RunBackground(
            token => ProcessManagedSessionRecoveriesAsync(profileId, work, token),
            "managed_session_recovery_failed");
    }

    private async Task ProcessManagedSessionRecoveriesAsync(
        Guid profileId,
        RecoveryWorkState work,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            lock (work.Gate)
            {
                if (!work.Requested)
                {
                    work.Running = false;
                    return;
                }

                work.Requested = false;
            }

            try
            {
                await RecoverManagedSessionAsync(profileId, cancellationToken).ConfigureAwait(true);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                lock (work.Gate)
                {
                    work.Running = false;
                }

                throw;
            }
            catch (Exception exception)
            {
                _logger?.Error("managed_session_recovery_attempt_failed", exception, new Dictionary<string, object?>
                {
                    ["profileId"] = profileId
                });
            }
        }
    }

    private void RunBackground(
        Func<CancellationToken, Task> operation,
        string safeFailureEvent)
    {
        if (!Dispatcher.CheckAccess())
        {
            if (_lifetime.IsCancellationRequested || Dispatcher.HasShutdownStarted)
            {
                return;
            }

            _ = Dispatcher.BeginInvoke(
                new Action(() => RunBackground(operation, safeFailureEvent)));
            return;
        }

        if (_lifetime.IsCancellationRequested)
        {
            return;
        }

        var task = RunBackgroundCoreAsync(operation, safeFailureEvent);
        lock (_backgroundGate)
        {
            _backgroundTasks.Add(task);
        }

        _ = task.ContinueWith(
            completed =>
            {
                lock (_backgroundGate)
                {
                    _backgroundTasks.Remove(completed);
                }
            },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private async Task RunBackgroundCoreAsync(
        Func<CancellationToken, Task> operation,
        string safeFailureEvent)
    {
        try
        {
            await operation(_lifetime.Token).ConfigureAwait(true);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            _logger?.Error(safeFailureEvent, exception);
        }
    }

    private async Task AwaitBackgroundTasksAsync()
    {
        while (true)
        {
            Task[] pending;
            lock (_backgroundGate)
            {
                pending = _backgroundTasks.ToArray();
            }

            if (pending.Length == 0)
            {
                return;
            }

            await Task.WhenAll(pending).ConfigureAwait(true);
        }
    }

    private void ShowMainWindow()
    {
        if (_membershipWindow is not null) { _membershipWindow.Activate(); return; }
        if (_membership is not null && !_membership.CanUse) { ShowMembership(); return; }
        _mainWindow?.ShowAndActivate();
    }

    private async void ToggleClickThrough()
    {
        if (_settingsRepository is null)
        {
            return;
        }

        try
        {
            await _settingsRepository.UpdateAsync(settings => settings with { ClickThrough = !settings.ClickThrough })
                .ConfigureAwait(true);
            await ReloadAndApplySettingsAsync().ConfigureAwait(true);
            _dashboard?.ShowToast(CurrentSettings.ClickThrough
                ? "Click-through enabled · use the tray menu to disable it"
                : "Click-through disabled");
        }
        catch (Exception exception)
        {
            _logger?.Error("click_through_toggle_failed", exception);
            _dashboard?.ShowToast("Click-through could not be changed");
        }
    }

    public async void RequestExit()
    {
        if (_dashboard?.CanExit == false)
        {
            _tray?.ShowNotification(
                "Operation still running",
                "Let the current account operation finish before exiting the monitor.");
            return;
        }

        if (!await CleanupAsync().ConfigureAwait(true))
        {
            _tray?.ShowNotification(
                "Task still running",
                "Exit was cancelled because a monitor-managed Codex task became active.");
            return;
        }

        Shutdown();
    }

    private Task<bool> CleanupAsync()
    {
        lock (_cleanupGate)
        {
            if (_cleanupTask is null
                || _cleanupTask.IsFaulted
                || _cleanupTask.IsCanceled
                || (_cleanupTask.IsCompletedSuccessfully && !_cleanupTask.Result))
            {
                _cleanupTask = CleanupCoreAsync();
            }

            return _cleanupTask;
        }
    }

    private async Task<bool> CleanupCoreAsync()
    {
        if (_switchCoordinator is not null)
        {
            try
            {
                // The coordinator performs the definitive running-task check
                // while holding its own gate. UI teardown only begins after the
                // resident process has been safely released.
                await _switchCoordinator.DisposeAsync().ConfigureAwait(true);
            }
            catch (InvalidOperationException exception)
                when (string.Equals(exception.Message, "managed_task_running", StringComparison.Ordinal))
            {
                _logger?.Warning("application_exit_refused_managed_task_running");
                return false;
            }
            catch (Exception exception)
            {
                _logger?.Error("managed_session_shutdown_failed", exception);
                return false;
            }
        }

        if (_mainWindow is { IsLoaded: true })
            await _mainWindow.SavePlacementAsync().ConfigureAwait(true);
        if (!_isDemo)
        {
            Microsoft.Win32.SystemEvents.PowerModeChanged -= OnPowerModeChanged;
            System.Net.NetworkInformation.NetworkChange.NetworkAvailabilityChanged -= OnNetworkAvailabilityChanged;
        }
        if (_monitor is not null) _monitor.AccountsChanged -= OnUsageChanged;
        SystemParameters.StaticPropertyChanged -= OnSystemParametersChanged;
        _lifetime.Cancel();
        foreach (var refresh in _notificationRefreshes.Values)
        {
            try
            {
                refresh.Cancel();
            }
            catch (ObjectDisposedException)
            {
            }
        }

        _dashboard?.Dispose();
        DisposeSolModel();
        _mainWindow?.AllowClose();
        _mainWindow?.Close();
        _tray?.Dispose();
        _tray = null;

        if (_scheduler is not null)
        {
            try
            {
                await _scheduler.DisposeAsync().ConfigureAwait(true);
            }
            catch (Exception exception)
            {
                _logger?.Error("refresh_scheduler_cleanup_failed", exception);
            }
        }

        try
        {
            await AwaitBackgroundTasksAsync().ConfigureAwait(true);
        }
        catch (Exception exception)
        {
            _logger?.Error("background_task_cleanup_failed", exception);
        }

        foreach (var pair in _notificationRefreshes.ToArray())
        {
            if (_notificationRefreshes.TryRemove(pair.Key, out var refresh))
            {
                try
                {
                    refresh.Dispose();
                }
                catch (ObjectDisposedException)
                {
                }
            }
        }

        _singleInstance?.Dispose();
        _singleInstance = null;
        if (_membership is not null) { _membership.Changed -= OnMembershipChanged; _membership.Dispose(); _membership = null; }
        _logger?.Info("application_stopped");
        _logger?.Dispose();
        _lifetime.Dispose();
        _cleanedUp = true;
        return true;
    }

    protected override void OnExit(ExitEventArgs e)
    {
        if (!_cleanedUp)
        {
            _lifetime.Cancel();
            foreach (var refresh in _notificationRefreshes.Values)
            {
                try
                {
                    refresh.Cancel();
                }
                catch (ObjectDisposedException)
                {
                }
            }

            _dashboard?.Dispose();
            DisposeSolModel();
            _tray?.Dispose();
            try
            {
                _scheduler?.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(3));
            }
            catch
            {
            }

            try
            {
                _switchCoordinator?.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(5));
            }
            catch
            {
            }

            _singleInstance?.Dispose();
            _logger?.Info("application_stopped");
            _logger?.Dispose();
            _cleanedUp = true;
        }

        base.OnExit(e);
    }

    private sealed class RecoveryWorkState
    {
        public object Gate { get; } = new();
        public bool Requested { get; set; }
        public bool Running { get; set; }
    }
}

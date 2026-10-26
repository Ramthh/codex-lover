using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using CodexAccountMonitor.Core.Models;
using CodexAccountMonitor.Core.Services;
using CodexAccountMonitor.Infrastructure;
using CodexAccountMonitor.Infrastructure.DesktopSwitching;
using CodexAccountMonitor.Infrastructure.Membership;

namespace CodexAccountMonitor.ViewModels;

public enum ToastSeverity { Information, Success, Warning, Error }

public sealed class DashboardViewModel : ObservableObject, IDisposable
{
    private readonly AccountMonitorService? _monitor;
    private readonly SettingsRepository? _settingsRepository;
    private readonly ProfileSwitchCoordinator? _switchCoordinator;
    private readonly DesktopAccountSwitchService? _desktopSwitcher;
    private readonly IUserInteractionService _interaction;
    private readonly bool _isDemo;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly DispatcherTimer _clockTimer;
    private readonly Dictionary<int, AccountCardViewModel> _cardsBySlot = new();
    private IReadOnlyList<PersistedAccount> _demoAccounts = Array.Empty<PersistedAccount>();
    private string _lastUpdatedText = "No successful refresh yet";
    private string? _toastMessage;
    private bool _isRefreshing;
    private bool _apiBillingActive;
    private Guid? _demoActiveId;
    private CancellationTokenSource? _toastLifetime;
    private int _disposed;
    private ToastSeverity _toastSeverity;
    private bool _comfortableDensity;
    private bool _opaqueBackground;

    public DashboardViewModel(
        AccountMonitorService? monitor,
        SettingsRepository? settingsRepository,
        ProfileSwitchCoordinator? switchCoordinator,
        IUserInteractionService interaction,
        bool isDemo = false,
        DesktopAccountSwitchService? desktopSwitcher = null)
    {
        _monitor = monitor;
        _settingsRepository = settingsRepository;
        _switchCoordinator = switchCoordinator;
        _desktopSwitcher = desktopSwitcher;
        _interaction = interaction;
        _isDemo = isDemo;

        AllAccounts = new ObservableCollection<AccountCardViewModel>();
        LeftAccounts = new ObservableCollection<AccountCardViewModel>();
        RightAccounts = new ObservableCollection<AccountCardViewModel>();
        RefreshAllCommand = new AsyncRelayCommand(RefreshAllAsync, () => !_isRefreshing);
        OpenSettingsCommand = new RelayCommand(_interaction.ShowSettings);
        DismissToastCommand = new RelayCommand(() => { _toastLifetime?.Cancel(); ToastMessage = null; });

        // Even before the repository has loaded, the widget presents one clear
        // action instead of ten placeholder cards.
        UpdateCards(Array.Empty<PersistedAccount>());

        if (_monitor is not null)
        {
            _monitor.AccountsChanged += OnAccountsChanged;
        }

        if (_switchCoordinator is not null)
        {
            _switchCoordinator.StatusChanged += OnSwitchStatusChanged;
        }

        _clockTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromSeconds(30)
        };
        _clockTimer.Tick += ClockTimerOnTick;
        _clockTimer.Start();
    }

    public ObservableCollection<AccountCardViewModel> LeftAccounts { get; }
    public ObservableCollection<AccountCardViewModel> RightAccounts { get; }
    public ObservableCollection<AccountCardViewModel> AllAccounts { get; }
    public ICommand RefreshAllCommand { get; }
    public ICommand OpenSettingsCommand { get; }
    public ICommand DismissToastCommand { get; }

    public int ConnectedCount => AllAccounts.Count(static account => account.IsConfigured);
    public string ConnectedLabel => ConnectedCount == 1 ? "1 account" : $"{ConnectedCount} accounts";
    public bool HasConnectedAccounts => ConnectedCount > 0;
    public int AttentionCount => AllAccounts.Count(static account => account.NeedsAttention);
    public bool HasAttention => AttentionCount > 0;
    public string AttentionSummary => AttentionCount > 0 ? $"{AttentionCount} need attention" : ConnectedCount > 0 ? "Usage up to date" : "Connect your first account";
    public string AppVersionLabel => $"v{typeof(DashboardViewModel).Assembly.GetName().Version?.ToString(3) ?? "1.0.0"}{(Application.Current is App { IsLocalTest: true } ? " · Model test" : string.Empty)}{(_isDemo ? " · Demo" : string.Empty)}";
    public bool ComfortableDensity { get => _comfortableDensity; private set => SetProperty(ref _comfortableDensity, value); }
    public bool OpaqueBackground { get => _opaqueBackground; private set => SetProperty(ref _opaqueBackground, value); }
    public ToastSeverity ToastSeverity { get => _toastSeverity; private set { if (SetProperty(ref _toastSeverity, value)) { OnPropertyChanged(nameof(ToastIsError)); OnPropertyChanged(nameof(ToastIsWarning)); OnPropertyChanged(nameof(ToastIsSuccess)); } } }
    public bool ToastIsError => ToastSeverity == ToastSeverity.Error;
    public bool ToastIsWarning => ToastSeverity == ToastSeverity.Warning;
    public bool ToastIsSuccess => ToastSeverity == ToastSeverity.Success;

    public string LastUpdatedText
    {
        get => _lastUpdatedText;
        private set => SetProperty(ref _lastUpdatedText, value);
    }

    public string? ToastMessage
    {
        get => _toastMessage;
        private set
        {
            if (SetProperty(ref _toastMessage, value))
            {
                OnPropertyChanged(nameof(HasToast));
            }
        }
    }

    public bool HasToast => !string.IsNullOrWhiteSpace(ToastMessage);

    public bool IsRefreshing
    {
        get => _isRefreshing;
        private set
        {
            if (SetProperty(ref _isRefreshing, value))
            {
                (RefreshAllCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
            }
        }
    }

    public bool ApiBillingActive
    {
        get => _apiBillingActive;
        private set => SetProperty(ref _apiBillingActive, value);
    }

    public bool CanExit => _switchCoordinator?.HasRunningTask != true && _desktopSwitcher?.IsBusy != true;

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        if (_isDemo)
        {
            _demoAccounts = DemoAccountSeed.Create();
            _demoActiveId = _demoAccounts[0].Profile.Id;
            UpdateCards(_demoAccounts);
            ApplyDemoActive();
            return;
        }

        if (_monitor is null || _settingsRepository is null || _switchCoordinator is null)
        {
            throw new InvalidOperationException("Dashboard services are unavailable.");
        }

        await _monitor.InitializeAsync(cancellationToken).ConfigureAwait(true);
        UpdateCards(_monitor.Accounts);
        _switchCoordinator.RegisterProfiles(_monitor.Accounts.Select(static account => account.Profile));
        var settings = await _settingsRepository.LoadAsync(cancellationToken).ConfigureAwait(true);
        ApplyDisplaySettings(settings);
        ApiBillingActive = settings.ApiFallback.Enabled && settings.ApiFallback.HasStoredCredential;
        // Version 1.4 uses the desktop sign-in for selection. Retire the old
        // monitor-owned resident session preference; scheduled refresh continues.
        if (settings.ActiveProfileId is not null)
            await _settingsRepository.UpdateAsync(value => value with { ActiveProfileId = null }, cancellationToken).ConfigureAwait(true);
        RefreshDesktopSelection();
    }

    public async Task ReloadSettingsAsync()
    {
        if (_settingsRepository is null)
        {
            return;
        }

        var settings = await _settingsRepository.LoadAsync().ConfigureAwait(true);
        ApplyDisplaySettings(settings);
        ApiBillingActive = settings.ApiFallback.Enabled && settings.ApiFallback.HasStoredCredential;
    }

    private void ApplyDisplaySettings(AppSettings settings)
    {
        ComfortableDensity = settings.ComfortableDensity;
        OpaqueBackground = settings.OpaqueBackground;
        foreach (var card in _cardsBySlot.Values)
            card.ApplyDisplaySettings(settings.RefreshIntervalMinutes, settings.ComfortableDensity);
        RaiseHealthSummary();
    }

    public void ShowToast(string message, TimeSpan? duration = null, ToastSeverity severity = ToastSeverity.Information)
    {
        _toastLifetime?.Cancel();
        _toastLifetime?.Dispose();
        _toastLifetime = new CancellationTokenSource();
        ToastSeverity = severity;
        ToastMessage = message;
        if (duration is not null || severity is not (ToastSeverity.Warning or ToastSeverity.Error))
            _ = ClearToastAsync(duration ?? TimeSpan.FromSeconds(5), _toastLifetime.Token);
    }

    private async Task ClearToastAsync(TimeSpan duration, CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(duration, cancellationToken).ConfigureAwait(true);
            ToastMessage = null;
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task RefreshAllAsync()
    {
        if (_isDemo)
        {
            IsRefreshing = true;
            await Task.Delay(450).ConfigureAwait(true);
            _demoAccounts = DemoAccountSeed.Create();
            UpdateCards(_demoAccounts);
            ApplyDemoActive();
            IsRefreshing = false;
            ShowToast("All demo accounts refreshed");
            return;
        }

        if (_monitor is null)
        {
            return;
        }
        if (!EnsureMembership()) return;
        IsRefreshing = true;
        var refreshStartedUtc = DateTimeOffset.UtcNow;
        try
        {
            await _monitor.RefreshAllAsync(_lifetime.Token).ConfigureAwait(true);
            var accounts = _monitor.Accounts;
            UpdateCards(accounts);
            var failed = accounts.Any(account => !string.IsNullOrWhiteSpace(account.Usage.SafeErrorCode));
            ShowToast(BuildRefreshSummary(accounts, refreshStartedUtc), severity: failed ? ToastSeverity.Warning : ToastSeverity.Success);
        }
        catch (Exception)
        {
            UpdateCards(_monitor.Accounts);
            ShowToast("Usage could not be refreshed. Retry the affected account below.", severity: ToastSeverity.Error);
        }
        finally
        {
            IsRefreshing = false;
        }
    }

    private async Task RefreshAccountAsync(AccountCardViewModel card)
    {
        if (_isDemo) { ShowToast("Demo usage is sample data"); return; }
        if (_monitor is null || card.ProfileId is not Guid id) return;
        if (!EnsureMembership(id)) return;
        try
        {
            await _monitor.RefreshAsync(id, _lifetime.Token).ConfigureAwait(true);
            UpdateCards(_monitor.Accounts);
            ShowToast(card.HasRefreshError ? $"{card.Alias}: {card.StatusMessage}" : $"{card.Alias} refreshed",
                severity: card.HasRefreshError ? ToastSeverity.Warning : ToastSeverity.Success);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch { ShowToast($"{card.Alias} could not be refreshed. Try again after checking its connection.", severity: ToastSeverity.Error); }
    }

    private async Task DeactivateAsync(AccountCardViewModel card)
    {
        if (_isDemo) { _demoActiveId = null; ApplyDemoActive(); return; }
        if (_monitor is null || card.ProfileId is not Guid id) return;
        try
        {
            await _monitor.PrepareProfileForMaintenanceAsync(id, _lifetime.Token).ConfigureAwait(true);
            if (_switchCoordinator is not null) ApplySwitchStatus(_switchCoordinator.Status);
            ShowToast($"Live updates stopped for {card.Alias}. Scheduled refresh continues.", severity: ToastSeverity.Success);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch { ShowToast("Live updates cannot stop while managed work is running. Try after it finishes.", severity: ToastSeverity.Warning); }
    }

    private async Task SwitchAsync(AccountCardViewModel card)
    {
        if (card.ProfileId is not Guid profileId) return;
        if (_isDemo)
        {
            card.IsSwitching = true;
            await Task.Delay(350).ConfigureAwait(true);
            _demoActiveId = profileId;
            ApplyDemoActive();
            ShowToast($"Demo: {card.Alias} selected. No Codex login was changed.", severity: ToastSeverity.Information);
            return;
        }
        if (!EnsureMembership(profileId)) return;
        if (_desktopSwitcher is null)
        {
            _interaction.ShowError("Switching unavailable", "The local Codex sign-in directory could not be verified. Account monitoring is still available.");
            return;
        }
        card.IsSwitching = true;
        try
        {
            if (await _interaction.SwitchDesktopAccountAsync(profileId).ConfigureAwait(true))
            {
                RefreshDesktopSelection();
                ShowToast($"Codex opened with {card.Alias}'s saved sign-in. Check its profile before starting a task.", severity: ToastSeverity.Success);
                await RefreshAccountAsync(card).ConfigureAwait(true);
            }
        }
        finally
        {
            // Recovery may have restored or preserved a sign-in even when launch failed.
            // Always reflect the on-disk result instead of leaving the old selection/spinner.
            card.IsSwitching = false;
            RefreshDesktopSelection();
        }
    }

    private async Task AddAsync(AccountCardViewModel card)
    {
        if (_isDemo)
        {
            ShowToast("Demo mode already contains ten sample profiles");
            return;
        }
        if (!EnsureMembership(enrollment: true)) return;
        if (await _interaction.EnrollAccountAsync(card.Slot).ConfigureAwait(true))
        {
            ShowToast("Account connected");
        }
    }

    private async Task ReauthenticateAsync(AccountCardViewModel card)
    {
        if (_isDemo || card.ProfileId is not Guid id)
        {
            ShowToast("Demo accounts do not require authentication");
            return;
        }
        if (!EnsureMembership(id)) return;
        if (await _interaction.ReauthenticateAsync(id).ConfigureAwait(true))
        {
            ShowToast($"{card.Alias} re-authenticated");
        }
    }

    private async Task RemoveAsync(AccountCardViewModel card)
    {
        if (_isDemo || card.ProfileId is not Guid id || _monitor is null)
        {
            ShowToast("Demo profiles are not persisted");
            return;
        }

        if (await _interaction.ConfirmRemoveAsync(card.Alias).ConfigureAwait(true))
        {
            try
            {
                await _monitor.RemoveAsync(id, _lifetime.Token).ConfigureAwait(true);
                ShowToast($"{card.Alias} removed from this monitor", severity: ToastSeverity.Success);
            }
            catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
            catch { ShowToast("Account could not be removed. If managed work is running, let it finish and retry.", severity: ToastSeverity.Error); }
        }
    }

    private async Task UseResetCreditAsync(AccountCardViewModel card)
    {
        if (_isDemo)
        {
            ShowToast("Reset credits are disabled in demo mode");
            return;
        }

        if (card.ProfileId is not Guid id || _monitor is null)
        {
            return;
        }
        if (!EnsureMembership(id)) return;
        if (!card.HasPendingResetCredit && !await _interaction.ConfirmResetCreditAsync(card.Alias).ConfigureAwait(true))
        {
            return;
        }

        try
        {
            var outcome = await _monitor.ConsumeResetCreditAsync(id, _lifetime.Token).ConfigureAwait(true);
            ShowToast(outcome switch
            {
                "reset" => "One reset credit was used",
                "alreadyRedeemed" => "The reset was already completed",
                "nothingToReset" => "No eligible limit window needs a reset",
                "noCredit" => "No reset credit is available",
                _ => "Reset outcome is not yet confirmed. Retry this same account to check safely; the saved attempt prevents a duplicate redemption."
            }, severity: outcome is "reset" or "alreadyRedeemed" ? ToastSeverity.Success : ToastSeverity.Warning);
        }
        catch (Exception)
        {
            ShowToast("Reset outcome could not be confirmed. Retry this same account to check safely; the saved attempt prevents a duplicate redemption.", severity: ToastSeverity.Warning);
        }
    }

    private void OnAccountsChanged(object? sender, EventArgs e)
    {
        Dispatch(() =>
        {
            // Read at dispatch time so an older queued notification cannot paint
            // over a newer multi-account refresh result.
            var accounts = _monitor?.Accounts ?? Array.Empty<PersistedAccount>();
            UpdateCards(accounts);
            _switchCoordinator?.RegisterProfiles(accounts.Select(static account => account.Profile));
        });
    }

    private bool EnsureMembership(Guid? profileId = null, bool enrollment = false)
    {
        try { _monitor?.AssertMembershipAccess(profileId, enrollment); return true; }
        catch (MembershipRequiredException exception)
        {
            ShowToast(exception.Message + " Open Membership to sign in or get Pro.", severity: ToastSeverity.Warning);
            return false;
        }
    }

    private void OnSwitchStatusChanged(object? sender, ProfileSwitchStatus status) =>
        Dispatch(() => ApplySwitchStatus(status));

    private void ApplySwitchStatus(ProfileSwitchStatus status) => RefreshDesktopSelection();

    private void RefreshDesktopSelection()
    {
        var selected = _desktopSwitcher?.SelectedProfileId;
        foreach (var account in AllAccounts)
        {
            account.IsActive = account.ProfileId is not null && account.ProfileId == selected;
            account.IsPending = false;
            account.IsSwitching = false;
        }
    }

    private void ApplyDemoActive()
    {
        foreach (var account in AllAccounts)
        {
            account.IsActive = account.ProfileId == _demoActiveId;
            account.IsPending = false;
            account.IsSwitching = false;
        }
    }

    internal void UpdateCards(IReadOnlyList<PersistedAccount> accounts)
    {
        var accountsBySlot = accounts.ToDictionary(static account => account.Profile.Slot);
        var visibleSlots = SelectVisibleSlots(accountsBySlot.Keys);
        foreach (var slot in visibleSlots)
            if (!_cardsBySlot.ContainsKey(slot)) _cardsBySlot[slot] = new AccountCardViewModel(slot,
                SwitchAsync, AddAsync, ReauthenticateAsync, RemoveAsync, UseResetCreditAsync,
                RefreshAccountAsync, DeactivateAsync, _interaction.ShowSettings);
        foreach (var (slot, card) in _cardsBySlot)
        {
            accountsBySlot.TryGetValue(slot, out var account);
            card.Update(account);
        }

        var visibleCards = visibleSlots
            .Select(slot => _cardsBySlot[slot])
            .ToArray();

        Synchronize(AllAccounts, visibleCards);
        Synchronize(LeftAccounts, visibleCards.Take(5));
        Synchronize(RightAccounts, visibleCards.Skip(5));

        if (_isDemo)
        {
            ApplyDemoActive();
        }
        else if (_switchCoordinator is not null)
        {
            ApplySwitchStatus(_switchCoordinator.Status);
        }

        OnPropertyChanged(nameof(ConnectedCount));
        OnPropertyChanged(nameof(ConnectedLabel));
        OnPropertyChanged(nameof(HasConnectedAccounts));
        RaiseHealthSummary();
        LastUpdatedText = BuildFreshnessSummary(accounts);
    }

    private void RaiseHealthSummary()
    {
        OnPropertyChanged(nameof(AttentionCount));
        OnPropertyChanged(nameof(HasAttention));
        OnPropertyChanged(nameof(AttentionSummary));
    }

    internal static string BuildFreshnessSummary(IReadOnlyList<PersistedAccount> accounts)
    {
        if (accounts.Count == 0) return "Local, private usage monitoring";
        var missing = accounts.Count(static account => account.Usage.LastSuccessfulRefreshUtc is null);
        if (missing > 0) return $"{missing} account{(missing == 1 ? "" : "s")} awaiting first update";
        var oldest = accounts.Min(static account => account.Usage.LastSuccessfulRefreshUtc)!.Value;
        return Relative(oldest).Replace("Last updated", accounts.Count > 1 ? "Oldest update" : "Updated", StringComparison.Ordinal);
    }

    internal static IReadOnlyList<int> SelectVisibleSlots(IEnumerable<int> configuredSlots)
    {
        var configured = configuredSlots
            .Where(static slot => slot >= 1)
            .Distinct()
            .OrderBy(static slot => slot)
            .ToList();

        var used = configured.ToHashSet();
        var next = 1;
        while (used.Contains(next)) next = checked(next + 1);
        configured.Add(next);

        return configured;
    }

    internal static string BuildRefreshSummary(
        IReadOnlyList<PersistedAccount> accounts,
        DateTimeOffset refreshStartedUtc)
    {
        if (accounts.Count == 0)
        {
            return "No connected accounts to refresh";
        }

        var attempted = accounts
            .Where(account => account.Usage.LastAttemptUtc is { } attempt
                              && attempt >= refreshStartedUtc)
            .ToArray();
        var failed = attempted.Count(static account =>
            !string.IsNullOrWhiteSpace(account.Usage.SafeErrorCode));
        var succeeded = attempted.Length - failed;
        var withoutLongTerm = attempted.Count(static account =>
            string.IsNullOrWhiteSpace(account.Usage.SafeErrorCode)
            && account.Usage.Snapshot?.Weekly is null);

        if (succeeded == accounts.Count && withoutLongTerm == 0)
        {
            return accounts.Count == 1
                ? "Account refreshed"
                : $"All {accounts.Count} accounts refreshed";
        }

        var parts = new List<string> { $"Refreshed {succeeded}/{accounts.Count}" };
        if (failed > 0)
        {
            parts.Add(failed == 1 ? "1 failed" : $"{failed} failed");
        }

        if (withoutLongTerm > 0)
        {
            parts.Add(withoutLongTerm == 1
                ? "1 has no reported long-term quota"
                : $"{withoutLongTerm} have no reported long-term quota");
        }

        return string.Join(" · ", parts);
    }

    private static void Synchronize(
        ObservableCollection<AccountCardViewModel> destination,
        IEnumerable<AccountCardViewModel> desiredItems)
    {
        var desired = desiredItems.ToArray();

        // Preserve existing view-model instances and issue only the collection
        // changes needed by the responsive ItemsControl.
        for (var index = 0; index < desired.Length; index++)
        {
            if (index < destination.Count && ReferenceEquals(destination[index], desired[index]))
            {
                continue;
            }

            var existingIndex = destination.IndexOf(desired[index]);
            if (existingIndex >= 0)
            {
                destination.Move(existingIndex, index);
            }
            else
            {
                destination.Insert(index, desired[index]);
            }
        }

        while (destination.Count > desired.Length)
        {
            destination.RemoveAt(destination.Count - 1);
        }
    }

    private async void ClockTimerOnTick(object? sender, EventArgs e)
    {
        foreach (var account in AllAccounts)
        {
            account.RefreshRelativeTime();
        }

        if (!_isDemo) RefreshDesktopSelection();
        RaiseHealthSummary();
        LastUpdatedText = BuildFreshnessSummary(_isDemo ? _demoAccounts : _monitor?.Accounts ?? Array.Empty<PersistedAccount>());

        if (_switchCoordinator?.Status.Phase == ProfileSwitchPhase.QueuedAfterCurrentTask)
        {
            try
            {
                var status = await _switchCoordinator.ApplyPendingIfIdleAsync(_lifetime.Token).ConfigureAwait(true);
                ApplySwitchStatus(status);
                if (status.Phase == ProfileSwitchPhase.Active && _settingsRepository is not null)
                {
                    try
                    {
                        await _settingsRepository.UpdateAsync(settings =>
                                settings with { ActiveProfileId = status.ActiveProfileId })
                            .ConfigureAwait(true);
                        ShowToast("Queued live updates started", severity: ToastSeverity.Success);
                    }
                    catch
                    {
                        ShowToast("Live updates started, but the startup preference could not be saved", severity: ToastSeverity.Warning);
                    }
                }
            }
            catch
            {
                ShowToast("Queued live updates could not start", severity: ToastSeverity.Warning);
            }
        }
    }

    private static string Relative(DateTimeOffset value)
    {
        var elapsed = DateTimeOffset.UtcNow - value;
        return elapsed < TimeSpan.FromMinutes(1)
            ? "Last updated just now"
            : elapsed < TimeSpan.FromHours(1)
                ? $"Last updated {(int)elapsed.TotalMinutes}m ago"
                : $"Last updated {value.ToLocalTime():MMM d, h:mm tt}";
    }

    private static void Dispatch(Action action)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
        {
            action();
        }
        else
        {
            _ = dispatcher.InvokeAsync(action);
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _lifetime.Cancel();
        _clockTimer.Stop();
        _clockTimer.Tick -= ClockTimerOnTick;
        if (_monitor is not null)
        {
            _monitor.AccountsChanged -= OnAccountsChanged;
        }

        if (_switchCoordinator is not null)
        {
            _switchCoordinator.StatusChanged -= OnSwitchStatusChanged;
        }

        _toastLifetime?.Cancel();
        _toastLifetime?.Dispose();
        _lifetime.Dispose();
    }
}

using System.Diagnostics;
using System.Windows.Threading;
using CodexAccountMonitor.Infrastructure;
using CodexAccountMonitor.Infrastructure.Membership;
using CodexAccountMonitor.Core.Models;

namespace CodexAccountMonitor;

public partial class App
{
    private SolModelWindow? _solModel;
    private DispatcherTimer? _modelTimer;
    private readonly Stopwatch _modelClock = new();
    private readonly ModelBubbleSchedule _modelSchedule = new();
    private ModelBubbleContent? _lastModelContent;

    private void InitializeSolModel()
    {
        _solModel = new SolModelWindow();
        _solModel.HideModelRequested += (_, _) => SetSolModelVisible(false);
        _solModel.ShowLimitsRequested += (_, _) => ShowSolModelLimits();
        _solModel.PositionChangedByUser += SaveModelPosition;
        _solModel.ModelChangedByUser += SaveModelCharacter;
        _solModel.ApplyPosition(CurrentSettings.ModelLeft, CurrentSettings.ModelTop);
        _modelClock.Start();
        _modelTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(250) };
        _modelTimer.Tick += (_, _) => UpdateSolModel();
        ApplySolModelSettings();
        if (IsLocalTest) _logger?.Info("model_initialized", new Dictionary<string, object?>
        {
            ["visible"] = _solModel.IsVisible,
            ["model"] = DesktopModels.Name(_solModel.SelectedModel),
            ["intervalSeconds"] = ModelBubbleSchedule.Interval.TotalSeconds,
            ["displaySeconds"] = ModelBubbleSchedule.DisplayDuration.TotalSeconds
        });
    }

    public void ToggleSolModel() => SetSolModelVisible(!_modelSchedule.IsEnabled);

    public IDisposable? SuspendSolModelForDialog() => _solModel?.SuspendForDialog();

    public void ShowSolModelLimits()
    {
        if (_solModel is null) return;
        SetSolModelVisible(true);
        _modelSchedule.Show(_modelClock.Elapsed);
        _lastModelContent = null;
        UpdateSolModel();
    }

    private void ApplySolModelSettings()
    {
        if (_solModel is null) return;
        _solModel.SetModel(DesktopModels.Parse(CurrentSettings.ModelCharacter));
        _solModel.ApplyPosition(CurrentSettings.ModelLeft, CurrentSettings.ModelTop);
        ApplySolModelVisibility(CurrentSettings.ModelVisible);
    }

    private void ApplySolModelVisibility(bool visible)
    {
        if (_solModel is null) return;
        if (visible)
        {
            if (!_modelSchedule.IsEnabled)
            {
                _solModel.ShowModel();
                _modelSchedule.Show(_modelClock.Elapsed);
                _lastModelContent = null;
            }
            _modelTimer?.Start();
            UpdateSolModel();
        }
        else
        {
            _modelSchedule.Hide();
            _modelTimer?.Stop();
            _solModel.HideModel();
            _lastModelContent = null;
        }
        _tray?.SetModelVisible(visible);
    }

    private async void SetSolModelVisible(bool visible)
    {
        ApplySolModelVisibility(visible);
        if (_settingsRepository is null) return;
        try { CurrentSettings = await _settingsRepository.UpdateAsync(settings => settings with { ModelVisible = visible }); }
        catch (Exception exception) { _logger?.Error("model_visibility_save_failed", exception); }
    }

    private async void SaveModelPosition(double left, double top)
    {
        if (_settingsRepository is null) return;
        try { CurrentSettings = await _settingsRepository.UpdateAsync(settings => settings with { ModelLeft = left, ModelTop = top }); }
        catch (Exception exception) { _logger?.Error("model_position_save_failed", exception); }
    }

    private async void SaveModelCharacter(DesktopModel model)
    {
        _lastModelContent = null;
        UpdateSolModel();
        if (_settingsRepository is null) return;
        try
        {
            CurrentSettings = await _settingsRepository.UpdateAsync(settings => settings with
                { ModelCharacter = DesktopModels.Name(model) });
            if (IsLocalTest) _logger?.Info("model_character_changed", new Dictionary<string, object?>
                { ["model"] = DesktopModels.Name(model) });
        }
        catch (Exception exception) { _logger?.Error("model_character_save_failed", exception); }
    }

    private void UpdateSolModel()
    {
        if (_solModel is null || !_modelSchedule.IsEnabled) return;
        if (_modelSchedule.Advance(_modelClock.Elapsed)) _lastModelContent = null;
        if (!_modelSchedule.IsVisible)
        {
            _solModel.HideBubble();
            _lastModelContent = null;
            return;
        }
        // Use exactly the dashboard's selected account and its cached latest usage.
        // The model never opens credentials, changes selection or starts network refreshes.
        var selected = _dashboard?.AllAccounts.FirstOrDefault(card => card.IsConfigured && card.IsActive);
        var access = _membership?.CanUse ?? _isDemo;
        if (access && !_isDemo)
        {
            try { _monitor?.AssertMembershipAccess(selected?.ProfileId); }
            catch (MembershipRequiredException) { access = false; }
        }
        var content = ModelUsageSummary.From(selected, access, _isDemo, DesktopModels.Name(_solModel.SelectedModel));
        if (content != _lastModelContent)
        {
            _lastModelContent = content;
            _solModel.ShowBubble(content);
            if (IsLocalTest) _logger?.Info("model_bubble_shown", new Dictionary<string, object?>
            {
                ["elapsedSeconds"] = Math.Round(_modelClock.Elapsed.TotalSeconds, 1),
                ["hasActiveAccount"] = selected is not null,
                ["hasUsage"] = selected?.HasFiveHour == true || selected?.HasWeekly == true,
                ["accessAvailable"] = access,
                ["demoMode"] = _isDemo
            });
        }
    }

    private void DisposeSolModel()
    {
        _modelTimer?.Stop();
        _modelTimer = null;
        _modelSchedule.Hide();
        _solModel?.Dispose();
        _solModel = null;
        _modelClock.Stop();
    }
}

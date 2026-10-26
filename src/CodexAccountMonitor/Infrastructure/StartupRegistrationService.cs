using Microsoft.Win32;

namespace CodexAccountMonitor.Infrastructure;

public sealed class StartupRegistrationService
{
    private const string KeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "CodexAccountMonitor";

    public async Task SetEnabledAsync(bool enabled)
    {
#if STORE
        if (StoreDistribution.IsStorePackage)
        {
            var task = await Windows.ApplicationModel.StartupTask.GetAsync(StoreDistribution.StartupTaskId);
            if (!enabled) { task.Disable(); return; }
            var state = await task.RequestEnableAsync();
            if (state is not (Windows.ApplicationModel.StartupTaskState.Enabled or Windows.ApplicationModel.StartupTaskState.EnabledByPolicy))
                throw new StartupPreferenceException("Windows has disabled automatic startup. Enable Codex Lover in Windows Settings > Apps > Startup, then try again.");
            return;
        }
#endif
        await Task.CompletedTask;
        using var key = Registry.CurrentUser.OpenSubKey(KeyPath, writable: true)
                        ?? Registry.CurrentUser.CreateSubKey(KeyPath, writable: true);
        if (enabled)
        {
            var executable = Environment.ProcessPath
                             ?? throw new InvalidOperationException("Application executable path is unavailable.");
            key.SetValue(ValueName, $"\"{executable}\" --minimized", RegistryValueKind.String);
        }
        else
        {
            key.DeleteValue(ValueName, throwOnMissingValue: false);
        }
    }

    public async Task<bool> IsEnabledAsync()
    {
#if STORE
        if (StoreDistribution.IsStorePackage)
        {
            var task = await Windows.ApplicationModel.StartupTask.GetAsync(StoreDistribution.StartupTaskId);
            return task.State is Windows.ApplicationModel.StartupTaskState.Enabled or Windows.ApplicationModel.StartupTaskState.EnabledByPolicy;
        }
#endif
        await Task.CompletedTask;
        using var key = Registry.CurrentUser.OpenSubKey(KeyPath, writable: false);
        return key?.GetValue(ValueName) is string;
    }
}

public sealed class StartupPreferenceException(string message) : InvalidOperationException(message);

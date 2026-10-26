using System.Security.Principal;
using System.Windows.Threading;

namespace CodexAccountMonitor.Infrastructure;

public sealed class SingleInstanceCoordinator : IDisposable
{
    private readonly Mutex _mutex;
    private readonly EventWaitHandle _activationEvent;
    private readonly EventWaitHandle _settingsEvent;
    private readonly EventWaitHandle _upgradeExitEvent;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly bool _ownsMutex;
    private Task? _listener;
    private bool _disposed;

    public SingleInstanceCoordinator(string? instanceSuffix = null)
    {
        var sid = WindowsIdentity.GetCurrent().User?.Value?.Replace('-', '_') ?? Environment.UserName;
        var suffix = string.IsNullOrEmpty(instanceSuffix) ? string.Empty : "_" + instanceSuffix;
        var mutexName = $"Local\\CodexAccountMonitor_{sid}{suffix}";
        var eventName = $"Local\\CodexAccountMonitor_Activate_{sid}{suffix}";
        // A named mutex can survive its primary process while another launch still
        // holds a handle. Existing does not mean owned: acquire it, including after
        // an abandoned owner, instead of activating a listener that no longer exists.
        _mutex = new Mutex(initiallyOwned: false, mutexName);
        try { _ownsMutex = _mutex.WaitOne(0); }
        catch (AbandonedMutexException) { _ownsMutex = true; }
        _activationEvent = new EventWaitHandle(false, EventResetMode.AutoReset, eventName);
        _settingsEvent = new EventWaitHandle(false, EventResetMode.AutoReset, eventName + "_Settings");
        _upgradeExitEvent = new EventWaitHandle(false, EventResetMode.AutoReset, eventName + "_UpgradeExit");
    }

    public bool IsPrimary => _ownsMutex;

    public void SignalPrimary(bool settings = false) => (settings ? _settingsEvent : _activationEvent).Set();

    public void StartActivationListener(Dispatcher dispatcher, Action activate, Action? settings = null, Action? exitForUpdate = null)
    {
        if (!IsPrimary || _listener is not null)
        {
            return;
        }

        _listener = Task.Factory.StartNew(
            () =>
            {
                var handles = new WaitHandle[] { _activationEvent, _settingsEvent, _upgradeExitEvent, _lifetime.Token.WaitHandle };
                while (true)
                {
                    var signaled = WaitHandle.WaitAny(handles);
                    if (signaled == 3) break;
                    dispatcher.BeginInvoke(signaled == 2 ? exitForUpdate ?? activate : signaled == 1 ? settings ?? activate : activate);
                }
            },
            _lifetime.Token,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _lifetime.Cancel();
        _listener?.Wait(TimeSpan.FromSeconds(1));
        _activationEvent.Dispose();
        _settingsEvent.Dispose();
        _upgradeExitEvent.Dispose();
        if (_ownsMutex)
        {
            _mutex.ReleaseMutex();
        }

        _mutex.Dispose();
        _lifetime.Dispose();
    }
}

using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace CodexAccountMonitor.Infrastructure.DesktopSwitching;

public interface IDesktopQuitRequester
{
    Task RequestQuitAsync(DesktopProcessInfo desktopRoot, Func<bool> revalidateIdentity, CancellationToken cancellationToken);
}

internal interface IDesktopQuitEnvironment
{
    void EnsureSameSession(int processId);
    void EnsureNoHeldModifiers();
    IntPtr ForegroundWindow { get; }
    bool IsEnabled(IntPtr window);
    void OpenDesktop(string executablePath);
    void SendQuit(IntPtr expectedWindow, int processId);
}

/// <summary>
/// Uses Codex's normal Windows Quit menu accelerator. Closing its last window
/// only hides supported builds to the tray. No private IPC or process termination.
/// </summary>
public sealed class WindowsDesktopQuitRequester : IDesktopQuitRequester
{
    private readonly IDesktopWindowController _windows;
    private readonly IDesktopQuitEnvironment _environment;
    private readonly TimeSpan _activationTimeout;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;

    public WindowsDesktopQuitRequester() : this(new WindowsDesktopWindowController(),
        new NativeQuitEnvironment(), TimeSpan.FromSeconds(3), Task.Delay) { }

    internal WindowsDesktopQuitRequester(IDesktopWindowController windows, IDesktopQuitEnvironment environment,
        TimeSpan activationTimeout, Func<TimeSpan, CancellationToken, Task> delay)
    {
        _windows = windows; _environment = environment;
        _activationTimeout = activationTimeout; _delay = delay;
    }

    public async Task RequestQuitAsync(DesktopProcessInfo desktopRoot, Func<bool> revalidateIdentity,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!revalidateIdentity()) throw new InvalidOperationException("Codex changed while preparing to quit. Retry the switch.");
        _environment.EnsureSameSession(desktopRoot.ProcessId);
        var activation = Stopwatch.StartNew();
        var requestedOpen = false;
        DesktopWindowInfo? focused = null;
        // Tray restore and foreground activation are asynchronous. A single 100ms
        // attempt can deliver Quit too early or reject a correctly restoring window.
        while (activation.Elapsed < _activationTimeout)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _environment.EnsureNoHeldModifiers();
            if (!revalidateIdentity()) throw new InvalidOperationException("Codex changed while preparing to quit. Retry the switch.");
            var candidate = WindowsDesktopWindowController.SelectMainWindow(_windows.GetWindows(desktopRoot.ProcessId));
            if (candidate is not null)
            {
                // Do not discard a disabled main window and quit through a helper.
                // A modal confirmation must be completed manually by the user.
                if (!candidate.Enabled || !_environment.IsEnabled(candidate.Handle))
                    throw new InvalidOperationException("Codex is waiting for a dialog. Finish or close it normally, then retry the switch. Your sign-in has not changed.");
                if (!candidate.RawVisible)
                {
                    if (!requestedOpen)
                    {
                        // A tray-hidden window needs the app's normal single-instance
                        // show/focus handling. Native ShowWindow alone is not evidence
                        // that Electron has restored its own activation state.
                        _environment.OpenDesktop(desktopRoot.ExecutablePath!);
                        requestedOpen = true;
                    }
                    await _delay(TimeSpan.FromMilliseconds(100), cancellationToken).ConfigureAwait(false);
                    continue;
                }
                _windows.ShowAndActivate(candidate, desktopRoot.ProcessId);
                await _delay(TimeSpan.FromMilliseconds(100), cancellationToken).ConfigureAwait(false);
                if (_environment.ForegroundWindow == candidate.Handle && _environment.IsEnabled(candidate.Handle)
                    && _windows.IsVisible(candidate, desktopRoot.ProcessId))
                {
                    focused = candidate;
                    break;
                }
            }
            else
            {
                if (!requestedOpen)
                {
                    // With no surviving Electron window, its single-instance open
                    // action recreates one. Identity was rechecked immediately above.
                    _environment.OpenDesktop(desktopRoot.ExecutablePath!);
                    requestedOpen = true;
                }
                await _delay(TimeSpan.FromMilliseconds(100), cancellationToken).ConfigureAwait(false);
            }
        }
        if (focused is null)
            throw new InvalidOperationException("Windows could not bring the Codex window forward for Quit. Select Open Codex, then retry the switch. Your sign-in has not changed.");
        cancellationToken.ThrowIfCancellationRequested();
        // Re-read process identity after activation, before sending one atomic key sequence.
        if (!revalidateIdentity()) throw new InvalidOperationException("Codex changed while preparing to quit. Retry the switch.");
        _environment.EnsureNoHeldModifiers();
        var currentWindow = _windows.GetWindows(desktopRoot.ProcessId).SingleOrDefault(window => window.Handle == focused.Handle);
        if (currentWindow is null || !currentWindow.IsMainWindowCandidate || !currentWindow.Enabled
            || _environment.ForegroundWindow != focused.Handle || !_environment.IsEnabled(focused.Handle)
            || !_windows.IsVisible(currentWindow, desktopRoot.ProcessId))
            throw new InvalidOperationException("Focus moved away from Codex. No Quit command was sent; retry the switch.");
        cancellationToken.ThrowIfCancellationRequested();
        _environment.SendQuit(focused.Handle, desktopRoot.ProcessId);
    }

    private sealed class NativeQuitEnvironment : IDesktopQuitEnvironment
    {
        public void EnsureSameSession(int processId)
        {
            using var target = Process.GetProcessById(processId);
            using var current = Process.GetCurrentProcess();
            if (target.SessionId != current.SessionId)
                throw new InvalidOperationException("Codex is running in another Windows session. Close it there before switching.");
        }
        public void EnsureNoHeldModifiers() => WindowsDesktopQuitRequester.EnsureNoHeldModifiers();
        public IntPtr ForegroundWindow => GetForegroundWindow();
        public bool IsEnabled(IntPtr window) => IsWindowEnabled(window);
        public void OpenDesktop(string executablePath)
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = executablePath, WorkingDirectory = Path.GetDirectoryName(executablePath),
                UseShellExecute = true, WindowStyle = ProcessWindowStyle.Normal
            });
        }
        public void SendQuit(IntPtr expectedWindow, int processId)
        {
            // Check the exact HWND again at the native input boundary. Another
            // window owned by the same Electron PID is not an authorized target.
            GetWindowThreadProcessId(expectedWindow, out var owner);
            if (expectedWindow == IntPtr.Zero || owner != processId || GetForegroundWindow() != expectedWindow
                || !IsWindowEnabled(expectedWindow) || !IsWindowVisible(expectedWindow) || IsIconic(expectedWindow))
                throw new InvalidOperationException("Focus moved away from Codex. No Quit command was sent; retry the switch.");
            SendQuitKeys();
        }
    }

    private static void SendQuitKeys()
    {
        Input[] keys = [Key(0x11), Key(0x51), Key(0x51, keyUp: true), Key(0x11, keyUp: true)];
        if (SendInput((uint)keys.Length, keys, Marshal.SizeOf<Input>()) != keys.Length)
        {
            Input[] releases = [Key(0x51, keyUp: true), Key(0x11, keyUp: true)];
            SendInput((uint)releases.Length, releases, Marshal.SizeOf<Input>());
            throw new InvalidOperationException("Windows did not accept Codex's Quit command. Quit Codex normally and retry.");
        }
    }

    private static void EnsureNoHeldModifiers()
    {
        foreach (var key in new[] { 0x10, 0x11, 0x12, 0x5B, 0x5C })
            if (GetAsyncKeyState(key) < 0)
                throw new InvalidOperationException("Release Ctrl, Alt, Shift and Windows keys, then retry the switch.");
    }
    private static Input Key(ushort key, bool keyUp = false) => new()
    { Type = 1, Data = new InputUnion { Keyboard = new KeyboardInput { VirtualKey = key, Flags = keyUp ? 2u : 0u } } };
    [StructLayout(LayoutKind.Sequential)] private struct Input { public uint Type; public InputUnion Data; }
    [StructLayout(LayoutKind.Explicit)] private struct InputUnion
    { [FieldOffset(0)] public KeyboardInput Keyboard; [FieldOffset(0)] public MouseInput Mouse; }
    [StructLayout(LayoutKind.Sequential)] private struct KeyboardInput
    { public ushort VirtualKey, Scan; public uint Flags, Time; public UIntPtr ExtraInfo; }
    [StructLayout(LayoutKind.Sequential)] private struct MouseInput
    { public int X, Y; public uint MouseData, Flags, Time; public UIntPtr ExtraInfo; }
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint pid);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr window);
    [DllImport("user32.dll")] private static extern bool IsWindowEnabled(IntPtr window);
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr window);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int key);
    [DllImport("user32.dll", SetLastError = true)] private static extern uint SendInput(uint count, Input[] inputs, int size);
}

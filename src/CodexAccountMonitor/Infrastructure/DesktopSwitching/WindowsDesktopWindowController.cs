using System.ComponentModel;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Text;

namespace CodexAccountMonitor.Infrastructure.DesktopSwitching;

internal sealed record DesktopWindowInfo(IntPtr Handle, long Area,
    bool RawVisible = true, bool Iconic = false, long RestoredArea = 0,
    bool Enabled = true, bool Owned = false, bool ToolWindow = false, bool NoActivate = false, bool HasCaption = true)
{
    internal bool IsMainWindowCandidate => !Owned && !ToolWindow && !NoActivate;
    internal long SelectionArea => Iconic && RestoredArea > 0 ? RestoredArea : Area;
}

internal interface IDesktopWindowController
{
    IReadOnlyList<DesktopWindowInfo> GetWindows(int processId);
    void ShowAndActivate(DesktopWindowInfo window, int processId);
    bool IsVisible(DesktopWindowInfo window, int processId);
}

internal sealed class WindowsDesktopWindowController : IDesktopWindowController
{
    private readonly Func<IntPtr, bool> _hasWindowCaption;

    public WindowsDesktopWindowController() : this(HasWindowCaption) { }
    internal WindowsDesktopWindowController(Func<IntPtr, bool> hasWindowCaption)
        => _hasWindowCaption = hasWindowCaption;

    public IReadOnlyList<DesktopWindowInfo> GetWindows(int processId)
    {
        if (!ProcessIdToSessionId((uint)processId, out var targetSession)
            || !ProcessIdToSessionId((uint)Environment.ProcessId, out var currentSession)
            || targetSession != currentSession) return [];
        var windows = new List<DesktopWindowInfo>();
        Exception? enumerationFailure = null;
        var enumerated = EnumWindows((window, _) =>
        {
            try
            {
                if (!BelongsTo(window, processId)) return true;
                var name = new StringBuilder(128);
                if (GetClassName(window, name, name.Capacity) != 0 && name.ToString() == "Chrome_WidgetWin_1"
                    && GetWindowRect(window, out var rect))
                {
                    var area = Math.Max(0L, (long)rect.Right - rect.Left) * Math.Max(0L, (long)rect.Bottom - rect.Top);
                    var placement = new WindowPlacement { Length = Marshal.SizeOf<WindowPlacement>() };
                    var restoredArea = GetWindowPlacement(window, ref placement) ? Area(placement.NormalPosition) : 0;
                    var extendedStyle = GetExtendedStyle(window);
                    if (area > 0 || restoredArea > 0) windows.Add(new(window, area,
                        IsWindowVisible(window), IsIconic(window), restoredArea, IsWindowEnabled(window),
                        GetWindow(window, 4) != IntPtr.Zero, // GW_OWNER, not the process owner.
                        (extendedStyle & 0x80) != 0, (extendedStyle & 0x08000000) != 0,
                        _hasWindowCaption(window)));
                }
                return true;
            }
            catch (Exception failure)
            {
                // Stop enumeration, then propagate on the managed side. A window
                // disappearing during a query must not unwind through Win32 frames
                // or make a partial inventory look like a valid selection.
                enumerationFailure = failure;
                return false;
            }
        }, IntPtr.Zero);
        if (enumerationFailure is not null) ExceptionDispatchInfo.Capture(enumerationFailure).Throw();
        if (!enumerated) throw new Win32Exception(Marshal.GetLastWin32Error());
        return windows;
    }

    internal static DesktopWindowInfo? SelectMainWindow(IEnumerable<DesktopWindowInfo> windows) => windows
        .Where(window => window.IsMainWindowCandidate)
        // Supported Electron builds lock the prewarmed helper to an empty title.
        // Check only the length: never collect the user's window caption text.
        .OrderByDescending(window => window.HasCaption)
        // WS_VISIBLE remains set on a minimized main window. Do not prefer a
        // non-iconic prewarmed helper over it, or rank its tiny minimized rectangle.
        .ThenByDescending(window => window.RawVisible)
        .ThenByDescending(window => window.SelectionArea)
        .FirstOrDefault();

    private static long Area(Rect rect) => Math.Max(0L, (long)rect.Right - rect.Left)
        * Math.Max(0L, (long)rect.Bottom - rect.Top);

    private static long GetExtendedStyle(IntPtr window) => IntPtr.Size == 8
        ? GetWindowLongPtr(window, -20).ToInt64() : GetWindowLong(window, -20);

    internal static bool HasWindowCaption(IntPtr window)
    {
        Marshal.SetLastPInvokeError(0);
        var length = GetWindowTextLength(window);
        var error = Marshal.GetLastPInvokeError();
        if (length == 0 && error != 0) throw new Win32Exception(error);
        return length > 0;
    }

    public void ShowAndActivate(DesktopWindowInfo window, int processId)
    {
        if (!BelongsTo(window.Handle, processId)) return;
        ShowWindowAsync(window.Handle, IsIconic(window.Handle) ? 9 : 5);
        RecoverOffScreenPlacement(window.Handle, processId);
        SetForegroundWindow(window.Handle);
    }

    private static void RecoverOffScreenPlacement(IntPtr window, int processId)
    {
        // Electron can restore a saved position on a disconnected monitor. Showing
        // that window succeeds, but it remains invisible to the user. Leave every
        // on-screen placement alone and never move a cloaked window on another
        // virtual desktop. A minimized restore may finish asynchronously; a later
        // launch verification pass will repair the restored placement if necessary.
        if (!BelongsTo(window, processId) || IsIconic(window)
            || MonitorFromWindow(window, 0) != IntPtr.Zero
            || DwmGetWindowAttribute(window, 14, out var cloaked, sizeof(int)) != 0 || cloaked != 0
            || !GetWindowRect(window, out var rect)) return;
        var nearest = MonitorFromWindow(window, 2); // MONITOR_DEFAULTTONEAREST
        var monitor = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (nearest == IntPtr.Zero || !GetMonitorInfo(nearest, ref monitor)) return;
        var workWidth = (long)monitor.Work.Right - monitor.Work.Left;
        var workHeight = (long)monitor.Work.Bottom - monitor.Work.Top;
        if (workWidth <= 0 || workHeight <= 0) return;
        var width = (int)Math.Clamp((long)rect.Right - rect.Left, 1, Math.Min(workWidth, int.MaxValue));
        var height = (int)Math.Clamp((long)rect.Bottom - rect.Top, 1, Math.Min(workHeight, int.MaxValue));
        var left = monitor.Work.Left + (int)((workWidth - width) / 2);
        var top = monitor.Work.Top + (int)((workHeight - height) / 2);
        // Recheck HWND ownership before changing placement; reused window handles
        // must never authorize moving another application's window.
        if (!BelongsTo(window, processId)) return;
        SetWindowPos(window, IntPtr.Zero, left, top, width, height, 0x0004 | 0x0010); // NOZORDER | NOACTIVATE
    }

    public bool IsVisible(DesktopWindowInfo window, int processId) => BelongsTo(window.Handle, processId)
        && IsWindowVisible(window.Handle) && !IsIconic(window.Handle)
        && MonitorFromWindow(window.Handle, 0) != IntPtr.Zero
        && DwmGetWindowAttribute(window.Handle, 14, out var cloaked, sizeof(int)) == 0 && cloaked == 0;

    private static bool BelongsTo(IntPtr window, int processId)
    {
        GetWindowThreadProcessId(window, out var owner);
        return owner == processId;
    }

    [StructLayout(LayoutKind.Sequential)] private struct Rect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct Point { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] private struct WindowPlacement
    { public int Length; public uint Flags, ShowCommand; public Point MinimumPosition, MaximumPosition; public Rect NormalPosition; }
    [StructLayout(LayoutKind.Sequential)] private struct MonitorInfo
    { public int Size; public Rect Monitor, Work; public uint Flags; }
    private delegate bool WindowCallback(IntPtr window, IntPtr state);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool EnumWindows(WindowCallback callback, IntPtr state);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint pid);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr window, StringBuilder name, int length);
    [DllImport("user32.dll", EntryPoint = "GetWindowTextLengthW", SetLastError = true)] private static extern int GetWindowTextLength(IntPtr window);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr window, out Rect rect);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr window);
    [DllImport("user32.dll")] private static extern bool IsWindowEnabled(IntPtr window);
    [DllImport("user32.dll")] private static extern IntPtr GetWindow(IntPtr window, uint command);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern IntPtr GetWindowLongPtr(IntPtr window, int index);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")] private static extern int GetWindowLong(IntPtr window, int index);
    [DllImport("user32.dll")] private static extern bool GetWindowPlacement(IntPtr window, ref WindowPlacement placement);
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr window);
    [DllImport("user32.dll")] private static extern bool ShowWindowAsync(IntPtr window, int command);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr window);
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromWindow(IntPtr window, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetWindowPos(IntPtr window, IntPtr insertAfter, int x, int y, int width, int height, uint flags);
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(IntPtr window, int attribute, out int value, int size);
    [DllImport("kernel32.dll")] private static extern bool ProcessIdToSessionId(uint processId, out uint sessionId);
}

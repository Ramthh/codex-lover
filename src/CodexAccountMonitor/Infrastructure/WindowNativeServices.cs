using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace CodexAccountMonitor.Infrastructure;

public static class WindowNativeServices
{
    private const int GwlExStyle = -20;
    private const int WsExTransparent = 0x20;
    private const int WsExToolWindow = 0x80;
    private const int WsExAppWindow = 0x40000;
    private const int DwmwaUseImmersiveDarkMode = 20;
    private const int DwmwaWindowCornerPreference = 33;
    private const int DwmwaSystemBackdropType = 38;
    private const uint MonitorDefaultToNearest = 2;

    public static void ApplyWindows11Backdrop(Window window)
    {
        var handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero)
        {
            return;
        }

        var enabled = 1;
        var round = 2;
        var transient = 3;
        _ = DwmSetWindowAttribute(handle, DwmwaUseImmersiveDarkMode, ref enabled, sizeof(int));
        _ = DwmSetWindowAttribute(handle, DwmwaWindowCornerPreference, ref round, sizeof(int));
        _ = DwmSetWindowAttribute(handle, DwmwaSystemBackdropType, ref transient, sizeof(int));
    }

    public static void SetClickThrough(Window window, bool enabled)
    {
        var handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero)
        {
            return;
        }

        var styles = GetWindowLongPtr(handle, GwlExStyle).ToInt64();
        styles = enabled ? styles | WsExTransparent : styles & ~WsExTransparent;
        _ = SetWindowLongPtr(handle, GwlExStyle, new IntPtr(styles));
    }

    /// <summary>
    /// Keeps the monitor in the desktop-widget category: tray-owned, absent
    /// from the taskbar and Alt+Tab, but still fully interactive when shown.
    /// This does not make the window topmost.
    /// </summary>
    public static void ApplyWidgetWindowStyle(Window window, bool trayOnly = true)
    {
        var handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero)
        {
            return;
        }

        var styles = GetWindowLongPtr(handle, GwlExStyle).ToInt64();
        styles = trayOnly ? (styles | WsExToolWindow) & ~WsExAppWindow : (styles & ~WsExToolWindow) | WsExAppWindow;
        _ = SetWindowLongPtr(handle, GwlExStyle, new IntPtr(styles));
    }

    public static void PlaceDesktopWidget(Window window, double maximumWidth = 1120)
    {
        var handle = new WindowInteropHelper(window).Handle;
        var monitor = MonitorFromWindow(handle, MonitorDefaultToNearest);
        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (monitor == IntPtr.Zero || !GetMonitorInfo(monitor, ref info))
        {
            return;
        }

        var dpi = GetDpiForWindow(handle);
        var scale = dpi > 0 ? dpi / 96.0 : 1.0;
        var workLeft = info.Work.Left / scale;
        var workTop = info.Work.Top / scale;
        var workWidth = (info.Work.Right - info.Work.Left) / scale;
        var workHeight = (info.Work.Bottom - info.Work.Top) / scale;

        window.MaxWidth = Math.Max(window.MinWidth, Math.Min(maximumWidth, workWidth - 24));
        window.MaxHeight = Math.Max(window.MinHeight, workHeight - 24);
        window.Width = Math.Min(window.Width, window.MaxWidth);
        window.Left = workLeft + Math.Max(12, workWidth - window.Width - 18);
        window.Top = workTop + 18;
    }

    public static void KeepDesktopWidgetOnScreen(Window window)
    {
        var handle = new WindowInteropHelper(window).Handle;
        var monitor = MonitorFromWindow(handle, MonitorDefaultToNearest);
        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (monitor == IntPtr.Zero || !GetMonitorInfo(monitor, ref info))
        {
            return;
        }

        var dpi = GetDpiForWindow(handle);
        var scale = dpi > 0 ? dpi / 96.0 : 1.0;
        var workLeft = info.Work.Left / scale;
        var workTop = info.Work.Top / scale;
        var workRight = info.Work.Right / scale;
        var workBottom = info.Work.Bottom / scale;
        var margin = 12d;
        window.Left = Math.Clamp(window.Left, workLeft + margin, Math.Max(workLeft + margin, workRight - window.Width - margin));
        window.Top = Math.Clamp(window.Top, workTop + margin, Math.Max(workTop + margin, workBottom - window.ActualHeight - margin));
    }

    public static void UpdateMonitorBounds(Window window)
    {
        var handle = new WindowInteropHelper(window).Handle;
        var monitor = MonitorFromWindow(handle, MonitorDefaultToNearest);
        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (handle == IntPtr.Zero || monitor == IntPtr.Zero || !GetMonitorInfo(monitor, ref info)) return;
        var dpi = GetDpiForWindow(handle);
        var scale = dpi > 0 ? dpi / 96.0 : 1.0;
        var width = Math.Max(200, (info.Work.Right - info.Work.Left) / scale - 24);
        var height = Math.Max(150, (info.Work.Bottom - info.Work.Top) / scale - 24);
        window.MinWidth = Math.Min(530, width);
        window.MinHeight = Math.Min(400, height);
        window.MaxWidth = Math.Max(window.MinWidth, Math.Min(1120, width));
        window.MaxHeight = Math.Max(window.MinHeight, height);
        window.Width = Math.Clamp(window.Width, window.MinWidth, window.MaxWidth);
    }

    public static void FitToMonitorWorkArea(Window window, double preferredWidth = 1268, double preferredHeight = 856)
    {
        var handle = new WindowInteropHelper(window).Handle;
        var monitor = MonitorFromWindow(handle, MonitorDefaultToNearest);
        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (monitor == IntPtr.Zero || !GetMonitorInfo(monitor, ref info))
        {
            return;
        }

        var dpi = GetDpiForWindow(handle);
        var scale = dpi > 0 ? dpi / 96.0 : 1.0;
        var workWidth = (info.Work.Right - info.Work.Left) / scale;
        var workHeight = (info.Work.Bottom - info.Work.Top) / scale;
        window.Width = Math.Min(preferredWidth, Math.Max(980, workWidth - 32));
        window.Height = Math.Min(preferredHeight, Math.Max(650, workHeight - 24));
        window.Left = info.Work.Left / scale + Math.Max(16, (workWidth - window.Width) / 2);
        window.Top = info.Work.Top / scale + Math.Max(12, (workHeight - window.Height) / 2);
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int valueSize);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLongPtr64(IntPtr hwnd, int index);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
    private static extern IntPtr GetWindowLong32(IntPtr hwnd, int index);

    private static IntPtr GetWindowLongPtr(IntPtr hwnd, int index) =>
        IntPtr.Size == 8 ? GetWindowLongPtr64(hwnd, index) : GetWindowLong32(hwnd, index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern IntPtr SetWindowLongPtr64(IntPtr hwnd, int index, IntPtr newStyle);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongW")]
    private static extern IntPtr SetWindowLong32(IntPtr hwnd, int index, IntPtr newStyle);

    private static IntPtr SetWindowLongPtr(IntPtr hwnd, int index, IntPtr newStyle) =>
        IntPtr.Size == 8 ? SetWindowLongPtr64(hwnd, index, newStyle) : SetWindowLong32(hwnd, index, newStyle);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hwnd);

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private struct MonitorInfo
    {
        public int Size;
        public Rect Monitor;
        public Rect Work;
        public uint Flags;
    }
}

using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using CodexAccountMonitor.Infrastructure.DesktopSwitching;

namespace CodexAccountMonitor.Tests;

/// <summary>Real Win32 placement checks use disposable windows owned by the test process, never Codex.</summary>
public sealed class WindowsDesktopWindowControllerTests
{
    [Fact]
    public void RestoringAnOffScreenWindowMovesItOntoAnAvailableMonitor()
    {
        using var window = new DisposableWindow(-32000, -32000, 640, 480);
        Assert.Equal(IntPtr.Zero, MonitorFromWindow(window.Handle, 0));
        var controller = new WindowsDesktopWindowController();
        // Reproduce the old restore-only behavior against a real HWND: Windows
        // accepts ShowWindow but leaves the saved off-screen placement untouched.
        ShowWindow(window.Handle, 5);
        Assert.False(controller.IsVisible(new(window.Handle, 640L * 480), Environment.ProcessId));
        controller.ShowAndActivate(new(window.Handle, 640L * 480), Environment.ProcessId);
        Assert.True(controller.IsVisible(new(window.Handle, 640L * 480), Environment.ProcessId),
            "A restored desktop window must be visible even after its previous monitor disappears.");
        var rect = Bounds(window.Handle);
        var monitor = WorkArea(window.Handle);
        Assert.True(rect.Left >= monitor.Left && rect.Top >= monitor.Top);
        Assert.True(rect.Right <= monitor.Right && rect.Bottom <= monitor.Bottom);
    }

    [Fact]
    public void VisibleWindowKeepsItsUserSelectedPlacement()
    {
        using var window = new DisposableWindow(100, 100, 420, 300);
        var work = WorkArea(window.Handle);
        Assert.True(SetWindowPos(window.Handle, IntPtr.Zero, work.Left + 10, work.Top + 10, 420, 300, 0x0014));
        var before = Bounds(window.Handle);
        var controller = new WindowsDesktopWindowController();
        controller.ShowAndActivate(new(window.Handle, 420L * 300), Environment.ProcessId);
        Assert.Equal(before, Bounds(window.Handle));
        Assert.True(controller.IsVisible(new(window.Handle, 420L * 300), Environment.ProcessId));
    }

    [Fact]
    public void AnotherProcessCannotAuthorizeMovingOrShowingTheWindow()
    {
        using var window = new DisposableWindow(-32000, -32000, 420, 300);
        var before = Bounds(window.Handle);
        var controller = new WindowsDesktopWindowController();
        controller.ShowAndActivate(new(window.Handle, 420L * 300), Environment.ProcessId + 1);
        Assert.Equal(before, Bounds(window.Handle));
        Assert.False(controller.IsVisible(new(window.Handle, 420L * 300), Environment.ProcessId + 1));
    }

    [Fact]
    public void MinimizedOffScreenWindowIsRestoredBeforePlacementRecovery()
    {
        using var window = new DisposableWindow(-32000, -32000, 640, 480);
        ShowWindow(window.Handle, 6); // SW_MINIMIZE, this exact disposable window only.
        Assert.True(IsIconic(window.Handle));
        var controller = new WindowsDesktopWindowController();
        var elapsed = Stopwatch.StartNew();
        do
        {
            controller.ShowAndActivate(new(window.Handle, 640L * 480), Environment.ProcessId);
            if (controller.IsVisible(new(window.Handle, 640L * 480), Environment.ProcessId)) break;
            PumpWindowMessages();
            Thread.Sleep(10);
        } while (elapsed.Elapsed < TimeSpan.FromSeconds(2));
        Assert.False(IsIconic(window.Handle));
        Assert.True(controller.IsVisible(new(window.Handle, 640L * 480), Environment.ProcessId));
    }

    [Fact]
    public void NativeMinimizedMainOutranksPrewarmWindowsByItsRestoredRectangle()
    {
        using var main = new ElectronFixtureWindow(1280, 620);
        using var visibleHelper = new ElectronFixtureWindow(960, 720, hasCaption: false);
        using var hiddenHelper = new ElectronFixtureWindow(1600, 1000, hasCaption: false);
        using var ownedPopup = new ElectronFixtureWindow(1800, 1100, owner: main.Handle);
        using var tool = new ElectronFixtureWindow(1800, 1100, extendedStyle: 0x80);
        using var noActivate = new ElectronFixtureWindow(1800, 1100, extendedStyle: 0x08000000);
        ShowWindow(main.Handle, 7); // SW_SHOWMINNOACTIVE, only a disposable test window.
        ShowWindow(visibleHelper.Handle, 4); // SW_SHOWNOACTIVATE
        ShowWindow(ownedPopup.Handle, 4);
        ShowWindow(tool.Handle, 4);
        ShowWindow(noActivate.Handle, 4);
        var windows = new WindowsDesktopWindowController().GetWindows(Environment.ProcessId);
        var minimized = Assert.Single(windows, window => window.Handle == main.Handle);
        Assert.True(minimized.RawVisible);
        Assert.True(minimized.Iconic);
        Assert.True(minimized.HasCaption);
        Assert.False(windows.Single(window => window.Handle == visibleHelper.Handle).HasCaption);
        Assert.True(minimized.RestoredArea > minimized.Area);
        Assert.True(minimized.SelectionArea > windows.Single(window => window.Handle == visibleHelper.Handle).Area);
        Assert.False(windows.Single(window => window.Handle == hiddenHelper.Handle).RawVisible);
        Assert.True(windows.Single(window => window.Handle == ownedPopup.Handle).Owned);
        Assert.True(windows.Single(window => window.Handle == tool.Handle).ToolWindow);
        Assert.True(windows.Single(window => window.Handle == noActivate.Handle).NoActivate);
        Assert.Equal(main.Handle, WindowsDesktopWindowController.SelectMainWindow(windows)?.Handle);

        // A modal dialog disables the main window. Selection must not fall back
        // to an enabled helper, where Quit could bypass the real user's dialog.
        EnableWindow(main.Handle, false);
        var disabledWindows = new WindowsDesktopWindowController().GetWindows(Environment.ProcessId);
        var selected = WindowsDesktopWindowController.SelectMainWindow(disabledWindows);
        Assert.Equal(main.Handle, selected?.Handle);
        Assert.False(selected!.Enabled);
    }

    [Fact]
    public void NativeHiddenTrayMainRemainsSelectable()
    {
        using var main = new ElectronFixtureWindow(1280, 620);
        using var helper = new ElectronFixtureWindow(960, 720);
        var windows = new WindowsDesktopWindowController().GetWindows(Environment.ProcessId);
        var selected = WindowsDesktopWindowController.SelectMainWindow(windows);
        Assert.Equal(main.Handle, selected?.Handle);
        Assert.False(selected!.RawVisible);
        Assert.False(selected.Iconic);
    }

    [Theory]
    [InlineData(4, false)] // Normal, without activating another application.
    [InlineData(7, false)] // Minimized.
    [InlineData(0, false)] // Hidden to tray.
    [InlineData(7, true)] // Disabled by a modal dialog.
    public void NativeSmallPrimaryOutranksAlreadyExposedBlankPrewarm(int showCommand, bool disabled)
    {
        using var main = new ElectronFixtureWindow(420, 300);
        using var prewarm = new ElectronFixtureWindow(960, 720, hasCaption: false);
        using var ownedPopup = new ElectronFixtureWindow(1800, 1100, owner: main.Handle);
        using var tool = new ElectronFixtureWindow(1800, 1100, extendedStyle: 0x80);
        using var noActivate = new ElectronFixtureWindow(1800, 1100, extendedStyle: 0x08000000);
        ShowWindow(main.Handle, showCommand);
        ShowWindow(prewarm.Handle, 4);
        ShowWindow(ownedPopup.Handle, 4);
        ShowWindow(tool.Handle, 4);
        ShowWindow(noActivate.Handle, 4);
        if (disabled) EnableWindow(main.Handle, false);
        var windows = new WindowsDesktopWindowController().GetWindows(Environment.ProcessId);
        var selected = WindowsDesktopWindowController.SelectMainWindow(windows);
        Assert.Equal(main.Handle, selected?.Handle);
        Assert.True(selected!.HasCaption);
        Assert.Equal(!disabled, selected.Enabled);
        var helper = windows.Single(window => window.Handle == prewarm.Handle);
        Assert.False(helper.HasCaption);
        Assert.True(helper.RawVisible);
        Assert.True(helper.SelectionArea > selected.SelectionArea);
    }

    [Fact]
    public void NativeAllBlankWindowsKeepMinimizedRestoredAreaFallback()
    {
        using var main = new ElectronFixtureWindow(1280, 620, hasCaption: false);
        using var helper = new ElectronFixtureWindow(960, 720, hasCaption: false);
        ShowWindow(main.Handle, 7);
        ShowWindow(helper.Handle, 4);
        var windows = new WindowsDesktopWindowController().GetWindows(Environment.ProcessId);
        Assert.All(windows, window => Assert.False(window.HasCaption));
        Assert.Equal(main.Handle, WindowsDesktopWindowController.SelectMainWindow(windows)?.Handle);
    }

    [Fact]
    public void FailedCaptionQueryIsNotMistakenForAValidBlankWindow()
    {
        var window = new ElectronFixtureWindow(420, 300, hasCaption: false);
        Marshal.SetLastPInvokeError(5);
        Assert.False(WindowsDesktopWindowController.HasWindowCaption(window.Handle));
        window.Dispose();
        var failure = Assert.Throws<Win32Exception>(() => WindowsDesktopWindowController.HasWindowCaption(window.Handle));
        Assert.Equal(1400, failure.NativeErrorCode); // ERROR_INVALID_WINDOW_HANDLE
    }

    [Fact]
    public void NativeEnumerationPropagatesCaptionFailureInsteadOfReturningPartialInventory()
    {
        using var window = new ElectronFixtureWindow(420, 300);
        var failure = new Win32Exception(1400);
        var queried = new List<IntPtr>();
        var controller = new WindowsDesktopWindowController(handle =>
        {
            queried.Add(handle);
            throw failure;
        });
        var propagated = Assert.Throws<Win32Exception>(() => controller.GetWindows(Environment.ProcessId));
        Assert.Same(failure, propagated);
        Assert.Equal(window.Handle, Assert.Single(queried));
        // The callback failure must not poison a later independent enumeration.
        Assert.Contains(new WindowsDesktopWindowController().GetWindows(Environment.ProcessId),
            candidate => candidate.Handle == window.Handle && candidate.HasCaption);
    }

    private static Rect Bounds(IntPtr window)
    {
        if (!GetWindowRect(window, out var rect)) throw new Win32Exception(Marshal.GetLastWin32Error());
        return rect;
    }

    private static Rect WorkArea(IntPtr window)
    {
        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        var monitor = MonitorFromWindow(window, 2);
        if (monitor == IntPtr.Zero || !GetMonitorInfo(monitor, ref info))
            throw new Win32Exception(Marshal.GetLastWin32Error());
        return info.Work;
    }

    private static void PumpWindowMessages()
    {
        while (PeekMessage(out var message, IntPtr.Zero, 0, 0, 1))
        {
            TranslateMessage(ref message);
            DispatchMessage(ref message);
        }
    }

    private sealed class DisposableWindow : IDisposable
    {
        public IntPtr Handle { get; }
        public DisposableWindow(int left, int top, int width, int height)
        {
            // Built-in STATIC avoids process-global class registrations. TOOLWINDOW
            // and NOACTIVATE keep this short-lived verification window off the taskbar.
            Handle = CreateWindowEx(0x00000080 | 0x08000000, "STATIC",
                "Codex Lover synthetic window verification", 0x10000000 | 0x00CF0000,
                left, top, width, height, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
            if (Handle == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
        }
        public void Dispose() => DestroyWindow(Handle);
    }

    private sealed class ElectronFixtureWindow : IDisposable
    {
        private static readonly WindowProcedure Procedure = DefWindowProc;
        private static readonly Lazy<ushort> Class = new(() =>
        {
            var definition = new WindowClass
            {
                Size = (uint)Marshal.SizeOf<WindowClass>(), Instance = GetModuleHandle(null),
                Procedure = Marshal.GetFunctionPointerForDelegate(Procedure), ClassName = "Chrome_WidgetWin_1"
            };
            var atom = RegisterClassEx(ref definition);
            if (atom == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
            return atom;
        });
        public IntPtr Handle { get; }
        public ElectronFixtureWindow(int width, int height, IntPtr owner = default, uint extendedStyle = 0, bool hasCaption = true)
        {
            _ = Class.Value;
            Handle = CreateWindowEx(extendedStyle, "Chrome_WidgetWin_1", hasCaption ? "Codex Lover synthetic selection test" : "",
                0x00CF0000, 100, 100, width, height, owner, IntPtr.Zero, GetModuleHandle(null), IntPtr.Zero);
            if (Handle == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
        }
        public void Dispose() => DestroyWindow(Handle);
    }

    private delegate IntPtr WindowProcedure(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] private struct WindowClass
    {
        public uint Size, Style; public IntPtr Procedure; public int ClassExtra, WindowExtra;
        public IntPtr Instance, Icon, Cursor, Background;
        public string? MenuName; public string ClassName; public IntPtr SmallIcon;
    }
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern ushort RegisterClassEx(ref WindowClass definition);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr DefWindowProc(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr GetModuleHandle(string? moduleName);
    [DllImport("user32.dll")] private static extern bool EnableWindow(IntPtr window, bool enabled);

    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct Rect(int Left, int Top, int Right, int Bottom);
    [StructLayout(LayoutKind.Sequential)] private struct MonitorInfo
    { public int Size; public Rect Monitor, Work; public uint Flags; }
    [StructLayout(LayoutKind.Sequential)] private struct Message
    { public IntPtr Window; public uint Id; public UIntPtr WParam; public IntPtr LParam; public uint Time; public int X, Y; public uint Private; }
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateWindowEx(uint extendedStyle, string className, string title, uint style,
        int x, int y, int width, int height, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr parameter);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool DestroyWindow(IntPtr window);
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetWindowRect(IntPtr window, out Rect rect);
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromWindow(IntPtr window, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetWindowPos(IntPtr window, IntPtr insertAfter, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool ShowWindow(IntPtr window, int command);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool IsIconic(IntPtr window);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool PeekMessage(out Message message, IntPtr window, uint minimum, uint maximum, uint flags);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool TranslateMessage(ref Message message);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr DispatchMessage(ref Message message);
}

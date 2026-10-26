using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using CodexAccountMonitor.Infrastructure;
using CodexAccountMonitor.Core.Models;

namespace CodexAccountMonitor;

/// <summary>A local animated companion. It has no account access or notification timer.</summary>
public partial class SolModelWindow : Window, IDisposable
{
    private readonly Window _bubble;
    private readonly Border _bubbleBorder;
    private readonly TextBlock _bubbleTitle;
    private readonly TextBlock _bubbleMessage;
    private readonly ModelDragGesture _dragGesture = new();
    private readonly DispatcherTimer _dragTimer;
    private int _dragButtonVirtualKey;
    private HwndSource? _source;
    private bool _animationsEnabled = true;
    private bool _dragging;
    private bool _disposed;
    private bool _hasPosition;
    private bool _modelRequested;
    private bool _bubbleRequested;
    private int _dialogSuspensions;

    public SolModelWindow()
    {
        InitializeComponent();
        _dragTimer = new DispatcherTimer(DispatcherPriority.Input, Dispatcher) { Interval = TimeSpan.FromMilliseconds(16) };
        _dragTimer.Tick += DragTimer_OnTick;
        Scene.ContextMenu.Opened += (_, _) => UpdateMotion();
        Scene.ContextMenu.Closed += (_, _) => UpdateMotion();
        _bubbleTitle = new TextBlock { FontSize = 13, FontWeight = FontWeights.SemiBold, Foreground = Brush("#DDFEF8"), TextWrapping = TextWrapping.Wrap };
        _bubbleMessage = new TextBlock { FontSize = 12, Foreground = Brush("#DCE5EA"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 7, 0, 0) };
        var dismiss = new Button { Content = "×", Width = 24, Height = 24, Padding = new Thickness(0), Background = Brushes.Transparent,
            BorderThickness = new Thickness(0), Foreground = Brush("#A8B6C4"), FontSize = 17, ToolTip = "Dismiss message", HorizontalAlignment = HorizontalAlignment.Right };
        dismiss.Click += (_, _) => HideBubble();
        var titleRow = new Grid();
        titleRow.ColumnDefinitions.Add(new ColumnDefinition());
        titleRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        titleRow.Children.Add(_bubbleTitle); titleRow.Children.Add(dismiss); Grid.SetColumn(dismiss, 1);
        var text = new StackPanel(); text.Children.Add(titleRow); text.Children.Add(_bubbleMessage);
        _bubbleBorder = new Border { Background = Brush("#F214202C"), BorderBrush = Brush("#7895DBD5"), BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(14), Padding = new Thickness(15, 12, 12, 14), Child = text };
        _bubble = new Window { Title = "Sol message", Width = 282, SizeToContent = SizeToContent.Height, WindowStyle = WindowStyle.None,
            ResizeMode = ResizeMode.NoResize, AllowsTransparency = true, Background = Brushes.Transparent, ShowActivated = false,
            ShowInTaskbar = false, Topmost = true, Content = _bubbleBorder };
        _bubble.SourceInitialized += (_, _) => ApplyNonActivatingStyle(_bubble);
        _bubble.SizeChanged += (_, _) => PositionBubble();
        SourceInitialized += (_, _) =>
        {
            ApplyNonActivatingStyle(this);
            _source = HwndSource.FromHwnd(new WindowInteropHelper(this).Handle);
            _source?.AddHook(WindowHook);
        };
        LocationChanged += (_, _) => { if (!_dragging) PositionBubble(); };
        IsVisibleChanged += (_, _) => UpdateMotion();
        SystemParameters.StaticPropertyChanged += OnSystemParametersChanged;
        Closed += (_, _) => DisposeCore();
    }

    public event Action<double, double>? PositionChangedByUser;
    public event Action<DesktopModel>? ModelChangedByUser;
    public DesktopModel SelectedModel => Character.Model;

    public void SetModel(DesktopModel model)
    {
        if (_disposed) return;
        Character.SetModel(model);
        var name = DesktopModels.Name(model);
        Title = $"{name} — Codex Lover Model";
        _bubble.Title = $"{name} message";
        Scene.ToolTip = $"{name} · Drag to move · Click for limits · Right-click to choose model";
        AnimationMenu.Header = $"Animate {name}";
        SolModelMenu.IsChecked = model == DesktopModel.Sol;
        LunaModelMenu.IsChecked = model == DesktopModel.Luna;
        TerraModelMenu.IsChecked = model == DesktopModel.Terra;
        AstraModelMenu.IsChecked = model == DesktopModel.Astra;
        UpdateMotion();
    }

    private void ChooseModel_OnClick(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem item) return;
        var model = DesktopModels.Parse(item.Tag as string);
        var changed = model != SelectedModel;
        SetModel(model);
        if (changed) ModelChangedByUser?.Invoke(model);
    }
    public event EventHandler? HideModelRequested;
    public event EventHandler? ShowLimitsRequested;
    public bool AnimationsEnabled
    {
        get => _animationsEnabled;
        set { _animationsEnabled = value; AnimationMenu.IsChecked = value; UpdateMotion(); }
    }

    public void ApplyPosition(double? x, double? y)
    {
        if (_disposed) return;
        if (x is double left && y is double top && double.IsFinite(left) && double.IsFinite(top))
        {
            Left = left; Top = top; _hasPosition = true;
        }
        else
        {
            var area = SystemParameters.WorkArea;
            Left = area.Right - Width - 26; Top = area.Bottom - Height - 18; _hasPosition = true;
        }
        if (IsVisible) { ClampToMonitor(this); PositionBubble(); }
    }

    public void ShowModel()
    {
        if (_disposed) return;
        _modelRequested = true;
        if (_dialogSuspensions > 0) return;
        ShowRequestedModel();
    }

    private void ShowRequestedModel()
    {
        if (!_hasPosition) ApplyPosition(null, null);
        if (!IsVisible) Show();
        ClampToMonitor(this);
        UpdateMotion();
        if (_bubbleRequested) { _bubble.Show(); PositionBubble(); }
    }

    public void HideModel()
    {
        if (_disposed) return;
        _modelRequested = false;
        _bubbleRequested = false;
        FinishPointerGesture(cancel: true, restorePresentation: false);
        _bubble.Hide(); Hide(); UpdateMotion();
    }

    /// <summary>Temporarily removes both topmost windows without changing the user's visibility preference.</summary>
    public IDisposable SuspendForDialog()
    {
        VerifyAccess();
        if (_disposed) return new DialogSuspension(null);
        if (++_dialogSuspensions == 1)
        {
            Scene.ContextMenu.IsOpen = false;
            FinishPointerGesture(cancel: true, restorePresentation: false);
            _bubble.Hide(); Hide(); UpdateMotion();
        }
        return new DialogSuspension(this);
    }

    private void ResumeAfterDialog()
    {
        VerifyAccess();
        if (_dialogSuspensions == 0) return;
        _dialogSuspensions--;
        if (!_disposed && _dialogSuspensions == 0 && _modelRequested) ShowRequestedModel();
    }

    private sealed class DialogSuspension(SolModelWindow? window) : IDisposable
    {
        private SolModelWindow? _window = window;
        public void Dispose()
        {
            var window = _window;
            _window = null;
            window?.ResumeAfterDialog();
        }
    }

    public void ShowBubble(ModelBubbleContent content)
    {
        if (_disposed) return;
        _bubbleTitle.Text = content.Title;
        _bubbleMessage.Text = content.Message;
        _bubbleBorder.BorderBrush = Brush(content.IsWarning ? "#DFE7B36B" : "#7895DBD5");
        _bubbleTitle.Foreground = Brush(content.IsWarning ? "#FFE2AF" : "#DDFEF8");
        _bubbleRequested = true;
        if (_dialogSuspensions > 0 || !IsVisible || _dragging) return;
        _bubble.Show(); _bubble.UpdateLayout(); PositionBubble();
        React();
    }

    public void HideBubble()
    {
        _bubbleRequested = false;
        if (!_disposed) _bubble.Hide();
    }

    private void PositionBubble()
    {
        if (_disposed || !_bubble.IsVisible || _dragging) return;
        var modelHandle = new WindowInteropHelper(this).Handle;
        var bubbleHandle = new WindowInteropHelper(_bubble).Handle;
        if (!GetWindowRect(modelHandle, out var model) || !GetWindowRect(bubbleHandle, out var bubble)) return;
        var monitor = MonitorFromWindow(modelHandle, 2);
        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (!GetMonitorInfo(monitor, ref info)) return;
        var width = bubble.Right - bubble.Left; var height = bubble.Bottom - bubble.Top;
        var left = model.Left + (int)((model.Right - model.Left) * .55) - width;
        var top = model.Top - height + 12;
        MoveWithinWorkArea(bubbleHandle, left, top, width, height, info.Work);
    }

    private bool CanAnimate => !_disposed && _animationsEnabled && SystemParameters.ClientAreaAnimation && IsVisible && !_dragging && !Scene.ContextMenu.IsOpen;

    private void UpdateMotion()
    {
        if (CanAnimate) Character.StartMotion();
        else Character.StopMotion();
    }

    private void StopMotion() => Character.StopMotion();

    private void React()
    {
        if (CanAnimate) Character.React();
    }

    private void Scene_OnMouseEnter(object sender, MouseEventArgs e) => React();

    private bool BeginPointerGesture(IntPtr hwnd, IntPtr messagePoint)
    {
        if (_disposed || _dragging || !IsVisible || !ScreenPointFromMessage(hwnd, messagePoint, out var pointer) ||
            !HitsCharacter(pointer.X, pointer.Y) || !GetWindowRect(hwnd, out var bounds)) return false;
        var scale = Math.Max(96u, GetDpiForWindow(hwnd)) / 96.0;
        _dragGesture.Begin(pointer.X, pointer.Y, bounds.Left, bounds.Top,
            (int)Math.Ceiling(SystemParameters.MinimumHorizontalDragDistance * scale),
            (int)Math.Ceiling(SystemParameters.MinimumVerticalDragDistance * scale));
        // WM_LBUTTONDOWN is logical left; GetAsyncKeyState uses the physical button.
        _dragButtonVirtualKey = GetSystemMetrics(23) != 0 ? 2 : 1; // SM_SWAPBUTTON / VK_RBUTTON / VK_LBUTTON
        _dragging = true; StopMotion(); _bubble.Hide(); Cursor = Cursors.SizeAll;
        SetCapture(hwnd);
        // A nonactivating background window cannot rely on full native capture.
        // Poll only for this held gesture so fast movement and release outside
        // the original window still work without activating Sol or a global hook.
        _dragTimer.Start();
        return true;
    }

    private void DragTimer_OnTick(object? sender, EventArgs e)
    {
        if (!_dragging) return;
        if (!IsVisible) { FinishPointerGesture(cancel: true); return; }
        // On release outside a background window, use the last held position;
        // cursor travel after release must not move Sol or turn a click into drag.
        if ((GetAsyncKeyState(_dragButtonVirtualKey) & 0x8000) == 0) { FinishPointerGesture(); return; }
        if (!GetCursorPos(out var pointer)) { FinishPointerGesture(cancel: true); return; }
        MovePointerGesture(pointer.X, pointer.Y);
    }

    private void MovePointerGesture(int x, int y)
    {
        if (_dragGesture.Move(x, y) is { } position)
            SetWindowPos(new WindowInteropHelper(this).Handle, IntPtr.Zero, position.X, position.Y, 0, 0, 0x0015);
    }

    private void FinishPointerGesture(bool cancel = false, bool restorePresentation = true)
    {
        if (!_dragging) return;
        var outcome = _dragGesture.Complete(cancel);
        _dragTimer.Stop();
        // Clear state before ReleaseCapture: it sends WM_CAPTURECHANGED synchronously.
        _dragging = false; Cursor = Cursors.Arrow;
        var hwnd = new WindowInteropHelper(this).Handle;
        if (GetCapture() == hwnd) ReleaseCapture();
        if (_disposed) return;
        ClampToMonitor(this);
        if (outcome == ModelDragOutcome.Drag)
        {
            _hasPosition = true;
            PositionChangedByUser?.Invoke(Left, Top);
        }
        else if (outcome == ModelDragOutcome.Click) ShowLimitsRequested?.Invoke(this, EventArgs.Empty);
        if (!restorePresentation || _disposed) return;
        UpdateMotion(); React();
        if (_bubbleRequested && IsVisible) { _bubble.Show(); PositionBubble(); }
    }

    private bool HitsCharacter(int screenX, int screenY)
    {
        var windowPoint = PointFromScreen(new Point(screenX, screenY));
        return Character.HitTestCharacter(TranslatePoint(windowPoint, Character));
    }

    private void ShowLimits_OnClick(object sender, RoutedEventArgs e) => ShowLimitsRequested?.Invoke(this, EventArgs.Empty);
    private void HideModel_OnClick(object sender, RoutedEventArgs e) => HideModelRequested?.Invoke(this, EventArgs.Empty);
    private void Animation_OnClick(object sender, RoutedEventArgs e) => AnimationsEnabled = AnimationMenu.IsChecked;
    private void OnSystemParametersChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SystemParameters.ClientAreaAnimation)) Dispatcher.InvokeAsync(UpdateMotion);
        if (e.PropertyName == nameof(SystemParameters.WorkArea) && IsVisible)
            Dispatcher.InvokeAsync(() => { ClampToMonitor(this); PositionBubble(); });
    }

    private IntPtr WindowHook(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message == 0x21) { handled = true; return new IntPtr(3); } // WM_MOUSEACTIVATE / MA_NOACTIVATE
        if (message == 0x84) // WM_NCHITTEST: explicitly interactive only on the character.
        {
            var packed = lParam.ToInt64();
            var hit = _dragging || HitsCharacter(unchecked((short)(packed & 0xffff)), unchecked((short)((packed >> 16) & 0xffff)));
            handled = true;
            return new IntPtr(hit ? 1 : -1); // HTCLIENT / HTTRANSPARENT
        }
        // Handle left input at the native window boundary. WPF DragMove depends
        // on routed button state and an activating system move loop; neither is
        // a reliable basis for this transparent WS_EX_NOACTIVATE companion.
        if ((message == 0x201 || message == 0x203) && BeginPointerGesture(hwnd, lParam)) handled = true; // DOWN / DBLCLK
        if (message == 0x202 && _dragging) // WM_LBUTTONUP
        {
            if (ScreenPointFromMessage(hwnd, lParam, out var pointer)) MovePointerGesture(pointer.X, pointer.Y);
            FinishPointerGesture(); handled = true;
        }
        if (message == 0x205 && !_dragging && ScreenPointFromMessage(hwnd, lParam, out var menuPoint)
            && HitsCharacter(menuPoint.X, menuPoint.Y)) // WM_RBUTTONUP
        {
            // A no-activate layered window cannot rely on WPF's routed context-menu gesture.
            // Open explicitly at the native click location after this message completes.
            var point = PointFromScreen(new Point(menuPoint.X, menuPoint.Y));
            Dispatcher.BeginInvoke(() =>
            {
                if (_disposed || !IsVisible) return;
                var menu = Scene.ContextMenu;
                menu.PlacementTarget = Scene;
                menu.Placement = System.Windows.Controls.Primitives.PlacementMode.RelativePoint;
                menu.HorizontalOffset = point.X;
                menu.VerticalOffset = point.Y;
                menu.IsOpen = true;
            }, DispatcherPriority.Input);
            handled = true;
        }
        if (message == 0x1F || (message == 0x215 && lParam != IntPtr.Zero && lParam != hwnd)) // CANCELMODE / capture stolen
            FinishPointerGesture(cancel: true);
        return IntPtr.Zero;
    }

    private static bool ScreenPointFromMessage(IntPtr hwnd, IntPtr lParam, out NativePoint point)
    {
        var packed = lParam.ToInt64();
        point = new NativePoint { X = unchecked((short)(packed & 0xffff)), Y = unchecked((short)((packed >> 16) & 0xffff)) };
        return ClientToScreen(hwnd, ref point);
    }

    private static SolidColorBrush Brush(string color) => new((Color)ColorConverter.ConvertFromString(color));

    private static void ApplyNonActivatingStyle(Window window)
    {
        WindowNativeServices.ApplyWidgetWindowStyle(window);
        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero) return;
        var style = GetWindowLongPtr(hwnd, -20).ToInt64();
        SetWindowLongPtr(hwnd, -20, new IntPtr(style | 0x08000000)); // WS_EX_NOACTIVATE
    }

    private static void ClampToMonitor(Window window)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero || !GetWindowRect(hwnd, out var bounds)) return;
        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (!GetMonitorInfo(MonitorFromWindow(hwnd, 2), ref info)) return;
        MoveWithinWorkArea(hwnd, bounds.Left, bounds.Top, bounds.Right - bounds.Left, bounds.Bottom - bounds.Top, info.Work);
    }

    private static void MoveWithinWorkArea(IntPtr hwnd, int left, int top, int width, int height, NativeRect work)
    {
        // Use native pixels throughout the clamp. Mixing WPF device-independent
        // coordinates with monitor pixels can jump on monitors with different DPI.
        left = Math.Clamp(left, work.Left + 8, Math.Max(work.Left + 8, work.Right - width - 8));
        top = Math.Clamp(top, work.Top + 8, Math.Max(work.Top + 8, work.Bottom - height - 8));
        SetWindowPos(hwnd, IntPtr.Zero, left, top, 0, 0, 0x0015); // NOSIZE | NOZORDER | NOACTIVATE
    }

    public void Dispose()
    {
        if (_disposed) return;
        DisposeCore(); Close();
    }

    private void DisposeCore()
    {
        if (_disposed) return;
        FinishPointerGesture(cancel: true, restorePresentation: false);
        _disposed = true; StopMotion();
        _dragTimer.Tick -= DragTimer_OnTick;
        Character.Dispose();
        SystemParameters.StaticPropertyChanged -= OnSystemParametersChanged;
        _source?.RemoveHook(WindowHook);
        _bubble.Close();
    }

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLongPtr(IntPtr hwnd, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern IntPtr SetWindowLongPtr(IntPtr hwnd, int index, IntPtr value);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr hwnd, out NativeRect rect);
    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out NativePoint point);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ClientToScreen(IntPtr hwnd, ref NativePoint point);
    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int virtualKey);
    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);
    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hwnd);
    [DllImport("user32.dll")]
    private static extern IntPtr SetCapture(IntPtr hwnd);
    [DllImport("user32.dll")]
    private static extern IntPtr GetCapture();
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ReleaseCapture();
    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint { public int X; public int Y; }
    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect { public int Left; public int Top; public int Right; public int Bottom; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private struct MonitorInfo { public int Size; public NativeRect Monitor; public NativeRect Work; public uint Flags; }
}

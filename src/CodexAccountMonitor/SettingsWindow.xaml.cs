using System.Windows;
using System.Windows.Input;
using CodexAccountMonitor.Infrastructure;
using CodexAccountMonitor.ViewModels;

namespace CodexAccountMonitor;

public partial class SettingsWindow : Window
{
    private readonly SettingsViewModel _viewModel;
    private bool _allowClose;

    public SettingsWindow(SettingsViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        DataContext = viewModel;
        viewModel.RequestClose += OnRequestClose;
        SourceInitialized += (_, _) => WindowNativeServices.ApplyWindows11Backdrop(this);
        Closed += (_, _) =>
        {
            viewModel.RequestClose -= OnRequestClose;
            viewModel.CancelPendingOperations();
        };
        Closing += (_, args) => args.Cancel = !viewModel.CanEdit && !_allowClose;
        Loaded += (_, _) => FitWindowToScreen();
        DpiChanged += (_, _) => FitWindowToScreen();
    }

    private async void SaveButton_OnClick(object sender, RoutedEventArgs e) =>
        _ = await _viewModel.SaveAsync().ConfigureAwait(true);

    private void BrowseCodex_OnClick(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Select the official Codex executable",
            Filter = "Codex executable (codex.exe)|codex.exe|Executable files (*.exe)|*.exe",
            CheckFileExists = true,
            Multiselect = false
        };
        if (dialog.ShowDialog(this) == true)
        {
            _viewModel.CodexExecutablePath = dialog.FileName;
        }
    }

    private async void RemoveApiKey_OnClick(object sender, RoutedEventArgs e) =>
        await _viewModel.RemoveStoredApiKeyAsync().ConfigureAwait(true);
    private void CloseButton_OnClick(object sender, RoutedEventArgs e) => Close();

    private void OnRequestClose(object? sender, bool saved)
    {
        _allowClose = true;
        DialogResult = saved;
        Close();
    }

    private void Header_OnMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed)
        {
            DragMove();
        }
    }

    private void FitWindowToScreen()
    {
        var handle = new System.Windows.Interop.WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero) return;
        var work = System.Windows.Forms.Screen.FromHandle(handle).WorkingArea;
        var dpi = System.Windows.Media.VisualTreeHelper.GetDpi(this);
        var availableWidth = Math.Max(320, work.Width / dpi.DpiScaleX - 24);
        var availableHeight = Math.Max(320, work.Height / dpi.DpiScaleY - 24);
        MinWidth = Math.Min(600, availableWidth);
        MinHeight = Math.Min(480, availableHeight);
        Width = Math.Min(Width, availableWidth);
        Height = Math.Min(Height, availableHeight);
        Left = Math.Clamp(Left, work.Left / dpi.DpiScaleX + 12,
            Math.Max(work.Left / dpi.DpiScaleX + 12, work.Right / dpi.DpiScaleX - Width - 12));
        Top = Math.Clamp(Top, work.Top / dpi.DpiScaleY + 12,
            Math.Max(work.Top / dpi.DpiScaleY + 12, work.Bottom / dpi.DpiScaleY - Height - 12));
    }
}

using System.Windows;
using System.Windows.Input;
using CodexAccountMonitor.Infrastructure;
using CodexAccountMonitor.ViewModels;

namespace CodexAccountMonitor;

public partial class AccountConnectionWindow : Window
{
    private readonly AccountConnectionViewModel _viewModel;

    public AccountConnectionWindow(AccountConnectionViewModel viewModel) : this(viewModel, null)
    {
    }

    internal AccountConnectionWindow(AccountConnectionViewModel viewModel, ResourceDictionary? resources)
    {
        if (resources is not null) Resources = resources;
        InitializeComponent();
        _viewModel = viewModel;
        DataContext = viewModel;
        viewModel.RequestClose += OnRequestClose;
        SourceInitialized += (_, _) => WindowNativeServices.ApplyWindows11Backdrop(this);
        Loaded += async (_, _) =>
        {
            var workArea = SystemParameters.WorkArea;
            MaxHeight = Math.Max(MinHeight, workArea.Height - 24);
            Height = Math.Min(Height, MaxHeight);
            Top = Math.Clamp(Top, workArea.Top + 12, Math.Max(workArea.Top + 12, workArea.Bottom - Height - 12));
            await viewModel.InitializeAsync();
        };
        Closed += (_, _) =>
        {
            viewModel.RequestClose -= OnRequestClose;
            viewModel.Dispose();
        };
    }

    private async void BrowseCodex_OnClick(object sender, RoutedEventArgs e)
    {
        if (_viewModel.IsBusy || _viewModel.Setup?.CanChoosePath != true) return;
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Select Codex from the official installation",
            Filter = "Codex executable (codex.exe)|codex.exe|Executable files (*.exe)|*.exe",
            CheckFileExists = true,
            Multiselect = false
        };
        if (dialog.ShowDialog(this) == true) await _viewModel.Setup.SelectExecutableAsync(dialog.FileName);
    }

    private void OnRequestClose(object? sender, bool success)
    {
        DialogResult = success;
        Close();
    }

    private void Header_OnMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed)
        {
            DragMove();
        }
    }
}

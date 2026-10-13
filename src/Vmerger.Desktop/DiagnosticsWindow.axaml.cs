using Avalonia.Controls;
using Avalonia.Interactivity;
using Vmerger.Desktop.ViewModels;

namespace Vmerger.Desktop;

public sealed partial class DiagnosticsWindow : Window
{
    private readonly MainWindowViewModel? _viewModel;

    public DiagnosticsWindow()
    {
        InitializeComponent();
    }

    public DiagnosticsWindow(MainWindowViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        DataContext = viewModel;
    }

    private void ClearExportOutputButton_OnClick(object? sender, RoutedEventArgs e)
    {
        _viewModel?.ClearExportOutput();
    }
}

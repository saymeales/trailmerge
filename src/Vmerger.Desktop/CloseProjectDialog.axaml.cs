using Avalonia.Controls;
using Avalonia.Interactivity;

namespace Vmerger.Desktop;

public sealed partial class CloseProjectDialog : Window
{
    public CloseProjectDialog() => InitializeComponent();

    private void CloseProject_OnClick(object? sender, RoutedEventArgs e) => Close(true);
    private void KeepProject_OnClick(object? sender, RoutedEventArgs e) => Close(false);
}

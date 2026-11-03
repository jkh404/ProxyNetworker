using Avalonia.Controls;
using Avalonia.Interactivity;

namespace ProxyNetworker.App;

public partial class MobileView : UserControl
{
    public MobileView()
    {
        InitializeComponent();
        // The Android activity host already applies its native safe-area insets.
        if (OperatingSystem.IsAndroid()) MobilePage.UsePlatformSafeArea = false;
    }

    private void OnTheme(object? sender, RoutedEventArgs args) => ClientViewActions.ToggleTheme();
    private async void OnImport(object? sender, RoutedEventArgs args) => await ClientViewActions.ImportAsync(this);
    private async void OnExport(object? sender, RoutedEventArgs args) => await ClientViewActions.ExportAsync(this);
}

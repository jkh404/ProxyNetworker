using Avalonia.Controls;
using Avalonia.Interactivity;

namespace ProxyNetworker.App;

public partial class MainView : UserControl
{
    public MainView()
    {
        InitializeComponent();
        SizeChanged += (_, args) =>
        {
            var compact = args.NewSize.Width < 600;
            FormGrid.ColumnDefinitions = new(compact ? "*" : "*,20,*");
            FormGrid.RowDefinitions = new(compact ? "Auto,Auto,Auto" : "Auto,Auto");
            Grid.SetColumn(NameField, compact ? 0 : 2);
            Grid.SetRow(NameField, compact ? 1 : 0);
            Grid.SetRow(TokenField, compact ? 2 : 1);
            Grid.SetColumnSpan(TokenField, compact ? 1 : 3);
        };
    }

    private void OnTheme(object? sender, RoutedEventArgs args) => ClientViewActions.ToggleTheme();

    private async void OnImport(object? sender, RoutedEventArgs args) => await ClientViewActions.ImportAsync(this);

    private async void OnExport(object? sender, RoutedEventArgs args) => await ClientViewActions.ExportAsync(this);
}

using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using ProxyNetworker.Client.Runtime;
using TKCode.Arc;
using TKCode.Arc.DesignTokens;

namespace ProxyNetworker.App;

public partial class App : Application
{
    public static Func<IClientController>? ControllerFactory { get; set; }
    public static string ConfigurationPath { get; set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ProxyNetworker", "appsettings.json");
    public static MainViewModel? ViewModel { get; private set; }
    /// <summary>Lets the desktop host own tray and window shutdown behavior without coupling Android to it.</summary>
    public static Action<Window, MainViewModel, IClassicDesktopStyleApplicationLifetime>? DesktopWindowCreated { get; set; }
#if DEBUG
    public static bool PreviewMobile { get; set; }
#endif

    public override void Initialize() => AvaloniaXamlLoader.Load(this);
    public override void OnFrameworkInitializationCompleted()
    {
        ViewModel = new(ControllerFactory?.Invoke() ?? throw new InvalidOperationException("No platform controller configured."),
            new ClientConfigurationStore(ConfigurationPath));
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            Control view = new MainView();
#if DEBUG
            if (PreviewMobile) view = new MobileView();
#endif
            view.DataContext = ViewModel;
            var window = new Window
            {
                Title = "ProxyNetworker", Width = 1040, Height = 760, MinWidth = 380, MinHeight = 560,
                Content = view
            };
#if DEBUG
            if (PreviewMobile) { window.Width = 412; window.Height = 860; window.MinWidth = 320; }
#endif
            ArcTokens.Bind(window, Window.BackgroundProperty, ArcDesignTokenKind.Background);
            desktop.MainWindow = window;
            if (DesktopWindowCreated is { } initializeDesktop) initializeDesktop(window, ViewModel, desktop);
            else
            {
                var closing = false;
                window.Closing += async (_, args) =>
                {
                    if (closing) return;
                    args.Cancel = true;
                    await ViewModel.ShutdownAsync();
                    closing = true;
                    window.Close();
                };
            }
        }
        else if (ApplicationLifetime is IActivityApplicationLifetime activity)
            activity.MainViewFactory = () => new MobileView { DataContext = ViewModel };
        else if (ApplicationLifetime is ISingleViewApplicationLifetime single)
            single.MainView = new MobileView { DataContext = ViewModel };
        _ = ViewModel.InitializeAsync();
        base.OnFrameworkInitializationCompleted();
    }
}

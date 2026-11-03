using Avalonia.Themes.Fluent;

namespace ProxyNetworker.App.Desktop;

internal static class DesktopTheme
{
    internal static App CreateApplication()
    {
        var application = new App();
        // Native tray menus use built-in Avalonia controls; Arc continues to theme its own controls.
        application.Styles.Add(new FluentTheme());
        return application;
    }
}

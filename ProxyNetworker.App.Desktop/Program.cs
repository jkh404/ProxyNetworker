using Avalonia;
using ProxyNetworker.App;
using ProxyNetworker.Client.Runtime;
using TKCode.Arc;
using TKCode.Arc.Mobile;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
#if DEBUG
using Spyglass.Agent;
#endif

namespace ProxyNetworker.App.Desktop;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ProxyNetworker");
        using var instance = DesktopInstance.TryAcquire(directory, () => Dispatcher.UIThread.Post(() =>
        {
            if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime { MainWindow: { } window })
            {
                window.Show();
                if (window.WindowState == WindowState.Minimized) window.WindowState = WindowState.Normal;
                window.Activate();
            }
        }));
        if (instance is null)
            return DesktopInstance.ActivateExistingAsync(directory).GetAwaiter().GetResult() ? 0 : 1;
        App.DesktopWindowCreated = (window, model, lifetime) => _ = new DesktopWindowLifetime(window, model, lifetime);
        var configIndex = Array.IndexOf(args, "--config");
        var config = configIndex >= 0 && configIndex + 1 < args.Length ? Path.GetFullPath(args[configIndex + 1])
            : new[] { Path.Combine(Environment.CurrentDirectory, "appsettings.json"), Path.Combine(AppContext.BaseDirectory, "appsettings.json") }
                .FirstOrDefault(File.Exists) ?? Path.Combine(directory, "appsettings.json");
        App.ConfigurationPath = config;
#if DEBUG
        App.PreviewMobile = args.Contains("--mobile-preview", StringComparer.Ordinal);
#endif
        App.ControllerFactory = () => new WorkerClientController(
            Path.Combine(AppContext.BaseDirectory, "worker", OperatingSystem.IsWindows() ? "ProxyNetworker.Worker.exe" : "ProxyNetworker.Worker"),
            Path.Combine(directory, "control"));
        var builder = AppBuilder.Configure<App>(DesktopTheme.CreateApplication).UsePlatformDetect().LogToTrace()
            .UseArc(arc => arc.UseArcControls().UseArcMobile()
                .WithTheme(theme => theme.Appearance(ArcAppearance.System).Accent(ArcAccentPreset.Green)));
#if DEBUG
        builder = builder.UseSpyglass();
#endif
        return builder.StartWithClassicDesktopLifetime(args);
    }
}

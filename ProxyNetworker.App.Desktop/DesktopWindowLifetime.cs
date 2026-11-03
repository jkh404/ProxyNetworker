using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.Input;
using TKCode.Arc;
using TKCode.Arc.DesignTokens;
using TKCode.Arc.Icons.Lucide;

namespace ProxyNetworker.App.Desktop;

internal sealed class DesktopWindowLifetime
{
    private readonly Window _window;
    private readonly MainViewModel _model;
    private readonly IClassicDesktopStyleApplicationLifetime _lifetime;
    private readonly TrayIcon _tray;
    private readonly NativeMenuItem _status;
    private bool _exiting, _allowClose, _disposed;

    internal DesktopWindowLifetime(Window window, MainViewModel model, IClassicDesktopStyleApplicationLifetime lifetime)
    {
        _window = window; _model = model; _lifetime = lifetime;
        var menu = new NativeMenu();
        _status = new(model.Status) { IsEnabled = false };
        var open = new NativeMenuItem("打开");
        open.Click += (_, _) => ShowWindow();
        var exit = new NativeMenuItem("退出") { Command = new AsyncRelayCommand(ExitAsync) };
        menu.Items.Add(_status); menu.Items.Add(new NativeMenuItemSeparator());
        menu.Items.Add(open);
        menu.Items.Add(new NativeMenuItem("连接") { Command = model.ConnectCommand });
        menu.Items.Add(new NativeMenuItem("断开") { Command = model.DisconnectCommand });
        menu.Items.Add(new NativeMenuItemSeparator()); menu.Items.Add(exit);
        _tray = new() { Menu = menu, ToolTipText = "ProxyNetworker - " + model.Status, IsVisible = true };
        _tray.Clicked += (_, _) => ShowWindow();
        TrayIcon.SetIcons(Application.Current!, new TrayIcons { _tray });
        UpdateIcon();
        window.Closing += OnClosing;
        model.PropertyChanged += OnModelChanged;
        if (ArcThemeManager.Current is { } theme) theme.StateChanged += OnThemeChanged;
        lifetime.Exit += (_, _) => Dispose();
    }
    private void OnClosing(object? sender, WindowClosingEventArgs args)
    {
        if (_allowClose) return;
        if (args.CloseReason == WindowCloseReason.OSShutdown)
        {
            _ = _model.ShutdownAsync();
            return;
        }
        args.Cancel = true;
        if (!_exiting && !_model.CanEditConfiguration && _tray.NativeMenuExporter is not null &&
            args.CloseReason == WindowCloseReason.WindowClosing)
            _window.Hide();
        else _ = ExitAsync();
    }
    internal async Task ExitAsync()
    {
        if (_exiting) return;
        _exiting = true;
        try
        {
            await _model.ShutdownAsync();
            _allowClose = true;
            Dispose();
            _lifetime.Shutdown();
        }
        catch (Exception exception)
        {
            _exiting = false;
            _model.Error = exception.Message;
            ShowWindow();
        }
    }
    private void ShowWindow()
    {
        _window.Show();
        if (_window.WindowState == WindowState.Minimized) _window.WindowState = WindowState.Normal;
        _window.Activate();
    }
    private void OnModelChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName != nameof(MainViewModel.Status)) return;
        _status.Header = _model.Status;
        _tray.ToolTipText = "ProxyNetworker - " + _model.Status;
    }
    private void OnThemeChanged(object? sender, EventArgs args) => Dispatcher.UIThread.Post(UpdateIcon);
    private void UpdateIcon()
    {
        if (_disposed) return;
        var glyph = new LucideIcon
        {
            Kind = LucideIconKind.Network, Size = 24, Width = 32, Height = 32,
            Foreground = new SolidColorBrush(ArcTokens.GetColor(_window, ArcDesignTokenKind.AccentStrong))
        };
        glyph.Measure(new Size(32, 32));
        glyph.Arrange(new Rect(0, 0, 32, 32));
        using var bitmap = new RenderTargetBitmap(new PixelSize(32, 32));
        bitmap.Render(glyph);
        var icon = new WindowIcon(bitmap);
        _window.Icon = icon; _tray.Icon = icon;
    }
    private void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _model.PropertyChanged -= OnModelChanged;
        if (ArcThemeManager.Current is { } theme) theme.StateChanged -= OnThemeChanged;
        _tray.Dispose();
    }
}

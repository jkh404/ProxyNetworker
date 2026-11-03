using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using ProxyNetworker.Client.Runtime;
using TKCode.Arc;
using TKCode.Arc.Mobile;
using ProxyNetworker.App.Desktop;

[assembly: AvaloniaTestApplication(typeof(ProxyNetworker.App.Tests.TestAppBuilder))]

namespace ProxyNetworker.App.Tests;

public static class TestAppBuilder
{
    public static AppBuilder BuildAvaloniaApp()
    {
        App.ControllerFactory = () => new TestController();
        App.ConfigurationPath = Path.Combine(Path.GetTempPath(), "pn-ui-unused-" + Guid.NewGuid().ToString("N") + ".json");
        return AppBuilder.Configure<App>(DesktopTheme.CreateApplication).UseHeadless(new AvaloniaHeadlessPlatformOptions())
            .UseArc(arc => arc.UseArcControls().UseArcMobile());
    }
}

public sealed class ClientUiTests
{
    [AvaloniaFact]
    public void NativeTrayMenuControlsHaveVisibleHeaderTemplates()
    {
        var exit = new Avalonia.Controls.MenuItem { Header = "退出" };
        var menu = new Avalonia.Controls.Menu();
        menu.Items.Add(new Avalonia.Controls.MenuItem { Header = "打开" });
        menu.Items.Add(exit);
        var window = new Window { Width = 260, Height = 200, Content = menu };
        try
        {
            window.Show();
            window.UpdateLayout();
            Assert.True(exit.Bounds.Width > 20);
            Assert.True(exit.Bounds.Height >= 20);
            Assert.Contains(exit.GetVisualDescendants().OfType<TextBlock>(), text => text.Text == "退出");
        }
        finally { window.Close(); }
    }

    [AvaloniaTheory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task BackgroundPolicyIsReadFromTheHostAndSettingsOnlyOpenOnExplicitCommands(bool exempt, bool notifications)
    {
        var controller = new TestBackgroundController { BackgroundState = new(exempt, notifications) };
        using var fixture = new Fixture(controller);
        await fixture.Model.InitializeAsync();
        Assert.True(fixture.Model.HasBackgroundSettings);
        Assert.Equal(exempt ? "不受限制" : "系统优化", fixture.Model.BackgroundBatteryStatus);
        Assert.Equal(notifications ? "已开启" : "已关闭", fixture.Model.BackgroundNotificationStatus);
        Assert.Equal(0, controller.BatterySettingsOpened);
        Assert.Equal(0, controller.NotificationSettingsOpened);
        fixture.Model.BatterySettingsCommand.Execute(null);
        fixture.Model.NotificationSettingsCommand.Execute(null);
        Assert.Equal(1, controller.BatterySettingsOpened);
        Assert.Equal(1, controller.NotificationSettingsOpened);
        controller.BackgroundState = new(!exempt, !notifications);
        fixture.Model.RefreshBackgroundSettings();
        Assert.Equal(exempt ? "系统优化" : "不受限制", fixture.Model.BackgroundBatteryStatus);
        Assert.Equal(notifications ? "已关闭" : "已开启", fixture.Model.BackgroundNotificationStatus);
        Assert.False(controller.Connected);
    }

    [AvaloniaFact]
    public void DesktopHostsDoNotAdvertiseMobileBackgroundSettings()
    {
        using var fixture = new Fixture();
        Assert.False(fixture.Model.HasBackgroundSettings);
        Assert.False(fixture.Model.BatterySettingsCommand.CanExecute(null));
        Assert.False(fixture.Model.NotificationSettingsCommand.CanExecute(null));
    }

    [AvaloniaFact]
    public async Task RecreatedViewRestoresTheAlreadyRunningServiceState()
    {
        var controller = new TestController();
        controller.Emit(ClientState.Stopped with
        {
            State = ClientConnectionState.Running, VirtualAddress = "198.18.11.4/29",
            InterfaceName = "Android VPN", Transport = "udp"
        });
        using var fixture = new Fixture(controller);
        await fixture.Model.InitializeAsync();
        Assert.Equal("运行中", fixture.Model.Status);
        Assert.Equal("198.18.11.4/29", fixture.Model.VirtualAddress);
        Assert.Equal("AES-GCM", fixture.Model.Encryption);
        Assert.False(fixture.Model.CanEditConfiguration);
        Assert.True(fixture.Model.CanDisconnect);
    }

    [AvaloniaFact]
    public async Task InitialStoppedStateDoesNotEraseInvalidConfigurationErrors()
    {
        using var fixture = new Fixture();
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(fixture.Path)!);
        await File.WriteAllTextAsync(fixture.Path, "{", TestContext.Current.CancellationToken);
        await fixture.Model.InitializeAsync();
        Assert.True(fixture.Model.HasError);
        Assert.Equal("未连接", fixture.Model.Status);
    }

    [AvaloniaFact]
    public async Task PortOnlySessionDoesNotClaimVirtualNetworkAesEncryption()
    {
        using var fixture = new Fixture();
        fixture.Controller.Emit(ClientState.Stopped with
        {
            State = ClientConnectionState.Running,
            PortTunnels = [new("tcp echo", true, "tcp", 47100)]
        });
        await Dispatcher.UIThread.InvokeAsync(() => { });
        Assert.Equal("不适用", fixture.Model.Encryption);
        Assert.Equal("端口隧道", fixture.Model.Transport);
    }

    [AvaloniaTheory]
    [InlineData(1040)]
    [InlineData(380)]
    public void DesktopRateTextFitsAndContentCanScrollToTheLastConfigurationRow(int width)
    {
        using var fixture = new Fixture();
        var view = new MainView { DataContext = fixture.Model };
        var window = new Window { Width = width, Height = 760, Content = view };
        try
        {
            window.Show();
            window.UpdateLayout();
            var rate = view.GetVisualDescendants().OfType<TextBlock>().First(text => text.Text == "0 B/s");
            Assert.True(rate.Bounds.Height >= rate.FontSize, $"Rate text is clipped: {rate.Bounds.Height} < {rate.FontSize}");
            var scroll = view.FindControl<ScrollViewer>("ContentScroll")!;
            Assert.True(scroll.Viewport.Height > 0);
            Assert.True(scroll.Viewport.Height < window.ClientSize.Height);
            Assert.True(scroll.Extent.Height > scroll.Viewport.Height);
            window.MouseMove(new Point(100, 300));
            window.MouseWheel(new Point(100, 300), new Vector(0, -3));
            window.UpdateLayout();
            Assert.True(scroll.Offset.Y > 0, "Mouse wheel did not scroll the content.");
            scroll.Offset = new Vector(0, scroll.Extent.Height);
            window.UpdateLayout();
            Assert.True(scroll.Offset.Y > 0);
            Assert.Equal(scroll.Extent.Height - scroll.Viewport.Height, scroll.Offset.Y, precision: 1);
        }
        finally { window.Close(); }
    }

    [AvaloniaTheory]
    [InlineData(320)]
    [InlineData(412)]
    public void MobileNavigationSelectsEachViewInsideTheViewport(int width)
    {
        using var fixture = new Fixture();
        var view = new MobileView { DataContext = fixture.Model };
        var window = new Window { Width = width, Height = 800, Content = view };
        try
        {
            window.Show();
            window.UpdateLayout();
            var navigation = view.FindControl<Tabbar>("MobileNavigation")!;
            var page = view.FindControl<TKCode.Arc.Mobile.Page>("MobilePage")!;
            Assert.True(navigation.Bounds.Width <= width);
            Assert.True(page.AvoidKeyboard);
            Assert.True(page.UsePlatformSafeArea);
            for (var index = 0; index < 4; index++)
            {
                navigation.SelectedIndex = index;
                window.UpdateLayout();
                Assert.Equal(index, fixture.Model.SelectedMobileView);
            }
            fixture.Model.SelectedMobileView = 2;
            window.UpdateLayout();
            Assert.True(view.FindControl<Input>("MobileServerInput")!.IsEffectivelyVisible);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task InvalidConfigurationDoesNotStartAControllerOrWriteAFile()
    {
        using var fixture = new Fixture();
        fixture.Model.NodeName = " ";
        await fixture.Model.ConnectCommand.ExecuteAsync(null);
        Assert.False(fixture.Controller.Connected);
        Assert.False(File.Exists(fixture.Path));
        Assert.NotEmpty(fixture.Model.TokenError);
        Assert.NotEmpty(fixture.Model.NodeNameError);
        Assert.True(fixture.Model.CanEditConfiguration);
        Assert.False(fixture.Model.Busy);
    }

    [AvaloniaFact]
    public async Task DisconnectCanCancelAConnectionThatIsStillStarting()
    {
        using var fixture = new Fixture();
        fixture.Model.Token = "vnet_test_only";
        fixture.Controller.BlockStartup = true;
        var connecting = fixture.Model.ConnectCommand.ExecuteAsync(null);
        await fixture.Controller.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(fixture.Model.CanDisconnect);
        Assert.True(fixture.Model.DisconnectCommand.CanExecute(null));
        await fixture.Model.DisconnectCommand.ExecuteAsync(null);
        await connecting;
        Assert.True(fixture.Controller.Disconnected);
        Assert.False(fixture.Model.Busy);
        Assert.True(fixture.Model.CanConnect);
        Assert.Empty(fixture.Model.Error);
    }

    [AvaloniaFact]
    public async Task ControllerEventsAreAppliedOnTheUiThreadAndDiagnosticsHideCredentials()
    {
        using var fixture = new Fixture();
        fixture.Model.Token = "vnet_test_only";
        await Task.Run(() =>
        {
            fixture.Controller.Emit(ClientState.Stopped with
            {
                State = ClientConnectionState.Running, VirtualAddress = "198.18.11.1/30",
                Peers = [new("198.18.11.2", "127.0.0.1:51820", "direct", 1.2)]
            });
            fixture.Controller.Log(new(DateTimeOffset.UtcNow, "Information", "vnet_test_only http://127.0.0.1:13301"));
        });
        await Dispatcher.UIThread.InvokeAsync(() => { });
        Assert.Equal("运行中", fixture.Model.Status);
        Assert.Single(fixture.Model.Peers);
        Assert.True(fixture.Model.HasLogs);
        var diagnostics = fixture.Model.ExportDiagnostics();
        Assert.DoesNotContain("vnet_test_only", diagnostics);
        Assert.DoesNotContain("127.0.0.1", diagnostics);
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string _directory = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "pn-ui-" + Guid.NewGuid().ToString("N"));
        internal string Path => System.IO.Path.Combine(_directory, "appsettings.json");
        internal TestController Controller { get; }
        internal MainViewModel Model { get; }
        internal Fixture(TestController? controller = null)
        {
            Controller = controller ?? new();
            Model = new(Controller, new ClientConfigurationStore(Path));
        }
        public void Dispose()
        {
            if (File.Exists(Path)) File.Delete(Path);
            if (Directory.Exists(_directory)) Directory.Delete(_directory);
        }
    }
}

internal class TestController : IClientController
{
    public ClientState State { get; private set; } = ClientState.Stopped;
    public event Action<ClientState>? StateChanged;
    public event Action<ClientLog>? LogReceived;
    internal bool Connected, Disconnected, BlockStartup;
    internal TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public async Task ConnectAsync(ProxyNetworkerClientConfig configuration, CancellationToken cancellationToken)
    {
        Connected = true;
        Started.TrySetResult();
        if (BlockStartup) await Task.Delay(Timeout.Infinite, cancellationToken);
        Emit(ClientState.Stopped with { State = ClientConnectionState.Running });
    }
    public Task DisconnectAsync(CancellationToken cancellationToken)
    {
        Disconnected = true;
        Emit(ClientState.Stopped);
        return Task.CompletedTask;
    }
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    internal void Emit(ClientState state) { State = state; StateChanged?.Invoke(state); }
    internal void Log(ClientLog log) => LogReceived?.Invoke(log);
}

internal sealed class TestBackgroundController : TestController, IClientBackgroundSettings
{
    internal ClientBackgroundState BackgroundState { get; set; } = new(false, false);
    internal int BatterySettingsOpened, NotificationSettingsOpened;
    public ClientBackgroundState GetBackgroundState() => BackgroundState;
    public void OpenBatterySettings() => BatterySettingsOpened++;
    public void OpenNotificationSettings() => NotificationSettingsOpened++;
}

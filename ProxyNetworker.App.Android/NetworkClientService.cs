using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.Net;
using Android.OS;
using System.Net.Sockets;
using ProxyNetworker.Client.Runtime;
using ProxyNetworker.Core;
using SharedApp = ProxyNetworker.App.App;

namespace ProxyNetworker.App.Android;

/// <summary>Foreground owner of virtual-network and port sessions, independent of the system VPN binding.</summary>
[Service(Name = "io.github.jkh404.proxynetworker.NetworkClientService")]
public sealed class NetworkClientService : Service, IClientPlatform
{
    internal const string StartAction = "io.github.jkh404.proxynetworker.START";
    internal const string StopAction = "io.github.jkh404.proxynetworker.STOP";
    internal const string ChannelId = "virtual-network";
    internal static NetworkClientService? Current { get; private set; }
    internal bool IsStopping => _lifetime.IsCancellationRequested;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();
    private LocalClientController? _controller;
    private VpnServiceBinding? _vpn;
    private ClientState? _failure;
    private string? _notificationStatus;
    private int _destroyed;
    public bool SupportsQuic => false;

    public override IBinder? OnBind(Intent? intent) => null;

    public override void OnCreate()
    {
        base.OnCreate(); Current = this;
        _controller = new(this);
        _controller.StateChanged += OnState;
        _controller.LogReceived += AndroidClientController.Shared.Log;
        var notifications = (NotificationManager)GetSystemService(NotificationService)!;
        notifications.CreateNotificationChannel(new NotificationChannel(ChannelId, "网络连接", NotificationImportance.Low));
    }

    public override StartCommandResult OnStartCommand(Intent? intent, StartCommandFlags flags, int startId)
    {
        if (intent?.Action == StopAction) { _ = StopNetworkAsync(); return StartCommandResult.NotSticky; }
        if ((intent is not null && intent.Action != StartAction) || !AndroidConnectionPreference.Requested)
        { StopSelf(); return StartCommandResult.NotSticky; }
        try
        {
            var notification = BuildNotification("正在启动");
            if (OperatingSystem.IsAndroidVersionAtLeast(34))
                StartForeground(1, notification, ForegroundService.TypeSpecialUse);
            else StartForeground(1, notification);
            _notificationStatus = "正在启动";
            _ = StartNetworkAsync();
        }
        catch (Exception exception)
        {
            _failure = ClientState.Stopped with { State = ClientConnectionState.Failed, Error = exception.Message };
            AndroidClientController.Shared.Publish(_failure);
            AndroidConnectionPreference.SetRequested(false);
            _lifetime.Cancel();
            StopSelf();
            return StartCommandResult.NotSticky;
        }
        return StartCommandResult.Sticky;
    }

    private async Task StartNetworkAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            _lifetime.Token.ThrowIfCancellationRequested();
            if (!AndroidConnectionPreference.Requested) { StopSelf(); return; }
            var controller = _controller ?? throw new ObjectDisposedException(nameof(NetworkClientService));
            if (controller.State.State is ClientConnectionState.Starting or ClientConnectionState.Running or ClientConnectionState.Reconnecting) return;
            var configuration = await new ClientConfigurationStore(SharedApp.ConfigurationPath).LoadAsync(_lifetime.Token).ConfigureAwait(false);
            if (configuration.VirtualNetwork is { Enabled: true } && VpnService.Prepare(this) is not null)
                throw new InvalidOperationException("VPN authorization was revoked. Open the app to authorize the connection again.");
            if (!AndroidConnectionPreference.Requested) { StopSelf(); return; }
            await controller.ConnectAsync(configuration, _lifetime.Token).ConfigureAwait(false);
        }
        catch (System.OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception exception)
        {
            if (Volatile.Read(ref _destroyed) == 0)
            {
                _failure = ClientState.Stopped with { State = ClientConnectionState.Failed, Error = exception.Message };
                AndroidClientController.Shared.Publish(_failure);
                AndroidConnectionPreference.SetRequested(false);
                _lifetime.Cancel();
                StopForeground(StopForegroundFlags.Remove); StopSelf();
            }
        }
        finally { _gate.Release(); }
    }

    internal async Task StopNetworkAsync()
    {
        if (ReferenceEquals(Current, this) && !AndroidConnectionPreference.SetRequested(false))
            AndroidClientController.Shared.Log(new(DateTimeOffset.UtcNow, "Warning", "Could not persist the stopped background connection state."));
        _lifetime.Cancel();
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_controller is not null) await _controller.DisconnectAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            AndroidClientController.Shared.Log(new(DateTimeOffset.UtcNow, "Error", $"VPN stop failed: {exception.GetType().Name}"));
        }
        finally
        {
            ReleaseVpnBinding();
            try
            {
                if (Volatile.Read(ref _destroyed) == 0)
                {
                    StopForeground(StopForegroundFlags.Remove); StopSelf();
                }
            }
            finally { _gate.Release(); }
        }
    }

    public override void OnTaskRemoved(Intent? rootIntent)
    {
        AndroidClientController.Shared.Log(new(DateTimeOffset.UtcNow, "Information", "App task removed; the foreground network service remains active."));
        base.OnTaskRemoved(rootIntent);
    }
    public override void OnDestroy()
    {
        Interlocked.Exchange(ref _destroyed, 1);
        _lifetime.Cancel();
        if (ReferenceEquals(Current, this)) Current = null;
        _ = DisposeNetworkAsync();
        base.OnDestroy();
    }
    private async Task DisposeNetworkAsync()
    {
        // Teardown shares the startup gate, so a late start cannot recreate a VPN after destruction.
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            var controller = _controller;
            _controller = null;
            if (controller is not null)
            {
                controller.StateChanged -= OnState;
                controller.LogReceived -= AndroidClientController.Shared.Log;
                await controller.DisposeAsync().ConfigureAwait(false);
            }
            if (Current is null) AndroidClientController.Shared.Publish(_failure ?? ClientState.Stopped);
        }
        catch (Exception exception)
        {
            AndroidClientController.Shared.Log(new(DateTimeOffset.UtcNow, "Error", $"VPN cleanup failed: {exception.GetType().Name}"));
        }
        finally { ReleaseVpnBinding(); _gate.Release(); }
    }

    private void ReleaseVpnBinding()
    {
        var binding = Interlocked.Exchange(ref _vpn, null);
        try { binding?.Dispose(); }
        catch (Exception exception)
        {
            AndroidClientController.Shared.Log(new(DateTimeOffset.UtcNow, "Error", $"VPN device binding cleanup failed: {exception.GetType().Name}"));
        }
    }

    private void OnState(ClientState state)
    {
        if (Volatile.Read(ref _destroyed) != 0 || !ReferenceEquals(Current, this)) return;
        if (state.State == ClientConnectionState.Failed) _failure = state;
        if (_failure is not null && state.State != ClientConnectionState.Failed) return;
        AndroidClientController.Shared.Publish(state);
        var status = state.State switch
        {
            ClientConnectionState.Running => "网络连接运行中",
            ClientConnectionState.Starting => "正在启动",
            ClientConnectionState.Reconnecting => "正在重连",
            ClientConnectionState.Stopping => "正在停止",
            ClientConnectionState.Failed => "连接失败",
            _ => "未连接"
        };
        if (_notificationStatus != status)
        {
            var manager = (NotificationManager)GetSystemService(NotificationService)!;
            manager.Notify(1, BuildNotification(status));
            _notificationStatus = status;
        }
        if (state.State == ClientConnectionState.Failed) _ = StopNetworkAsync();
    }
    private Notification BuildNotification(string status)
    {
        var open = PendingIntent.GetActivity(this, 0, new Intent(this, typeof(MainActivity))
            .AddFlags(ActivityFlags.SingleTop | ActivityFlags.ClearTop),
            PendingIntentFlags.Immutable | PendingIntentFlags.UpdateCurrent);
        var stop = PendingIntent.GetService(this, 1, new Intent(this, typeof(NetworkClientService)).SetAction(StopAction),
            PendingIntentFlags.Immutable | PendingIntentFlags.UpdateCurrent);
        return new Notification.Builder(this, ChannelId)
            .SetContentTitle("ProxyNetworker").SetContentText(status)
            .SetSmallIcon(global::Android.Resource.Drawable.IcDialogInfo)
            .SetContentIntent(open).SetOngoing(true).SetOnlyAlertOnce(true)
            .AddAction(new Notification.Action.Builder(null, "断开", stop).Build()).Build();
    }

    public async Task<ITunDevice> CreateTunAsync(TunDeviceOptions options, Action<ProxyNetworkerLogEntry> logger, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_vpn is { IsDisconnected: true }) { _vpn.Dispose(); _vpn = null; }
        _vpn ??= new VpnServiceBinding(this);
        var service = await _vpn.ConnectAsync(cancellationToken).ConfigureAwait(false);
        service.AttachOwner(this);
        service.EnterForeground(BuildNotification(_notificationStatus ?? "正在启动"));
        return new AndroidTunDevice(service, options, logger);
    }
    public void ConfigureTransportSocket(Socket socket)
    {
        var service = _vpn?.Service ?? throw new IOException("The Android VPN device service is not connected.");
        if (!service.Protect(checked((int)socket.Handle)))
            throw new IOException("Android could not protect the tunnel transport socket.");
    }
}

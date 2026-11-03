using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.Net;
using Android.OS;

namespace ProxyNetworker.App.Android;

/// <summary>System-bound VPN device; the independent foreground session owns its lifetime.</summary>
[Service(Name = "io.github.jkh404.proxynetworker.NetworkVpnService")]
public sealed class NetworkVpnService : VpnService
{
    internal const string SessionBindingAction = "io.github.jkh404.proxynetworker.BIND_DEVICE";
    private SessionBinder? _binder;
    private NetworkClientService? _owner;
    private int _destroyed;
    internal bool IsAlive => Volatile.Read(ref _destroyed) == 0;

    public override IBinder? OnBind(Intent? intent) => intent?.Action == SessionBindingAction
        ? _binder ??= new SessionBinder(this)
        : base.OnBind(intent);

    internal void AttachOwner(NetworkClientService owner) => Volatile.Write(ref _owner, owner);

    internal void EnterForeground(Notification notification)
    {
        ObjectDisposedException.ThrowIf(!IsAlive, this);
        if (OperatingSystem.IsAndroidVersionAtLeast(34))
            StartForeground(1, notification, ForegroundService.TypeSpecialUse);
        else StartForeground(1, notification);
    }

    public override void OnRevoke()
    {
        if (Volatile.Read(ref _owner) is { } owner && ReferenceEquals(NetworkClientService.Current, owner))
            _ = owner.StopNetworkAsync();
        base.OnRevoke();
    }

    public override void OnDestroy()
    {
        Interlocked.Exchange(ref _destroyed, 1);
        Volatile.Write(ref _owner, null);
        _binder?.Dispose(); _binder = null;
        base.OnDestroy();
    }

    internal sealed class SessionBinder(NetworkVpnService service) : Binder
    {
        internal NetworkVpnService Service { get; } = service;
    }
}

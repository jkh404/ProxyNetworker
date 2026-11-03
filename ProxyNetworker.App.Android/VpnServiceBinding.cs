using Android.Content;
using Android.OS;

namespace ProxyNetworker.App.Android;

/// <summary>Owns the session's in-process device binding; it does not start a second process.</summary>
internal sealed class VpnServiceBinding(Context context) : Java.Lang.Object, IServiceConnection
{
    private readonly TaskCompletionSource _connected = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private NetworkVpnService? _service;
    private bool _bound;
    private int _disposed;
    internal NetworkVpnService? Service => Volatile.Read(ref _service);
    internal bool IsDisconnected => _connected.Task.IsCompleted && Service is not { IsAlive: true };

    internal async Task<NetworkVpnService> ConnectAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        if (!_bound)
        {
            using var intent = new Intent(context, typeof(NetworkVpnService)).SetAction(NetworkVpnService.SessionBindingAction);
            _bound = context.BindService(intent, this, Bind.AutoCreate);
            if (!_bound) throw new IOException("Android could not bind the VPN device service.");
        }
        await _connected.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        return Service is { IsAlive: true } service ? service
            : throw new IOException("The Android VPN device service disconnected.");
    }

    public void OnServiceConnected(ComponentName? name, IBinder? binder)
    {
        if (Volatile.Read(ref _disposed) != 0) return;
        if (binder is not NetworkVpnService.SessionBinder session)
        {
            _connected.TrySetException(new IOException("Android returned an unexpected VPN device binder."));
            return;
        }
        Volatile.Write(ref _service, session.Service);
        _connected.TrySetResult();
    }

    public void OnServiceDisconnected(ComponentName? name)
    {
        Volatile.Write(ref _service, null);
        _connected.TrySetException(new IOException("The Android VPN device service disconnected."));
    }

    public void OnBindingDied(ComponentName? name) => OnServiceDisconnected(name);
    public void OnNullBinding(ComponentName? name) => OnServiceDisconnected(name);

    protected override void Dispose(bool disposing)
    {
        if (disposing && Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            Volatile.Write(ref _service, null);
            _connected.TrySetCanceled();
            if (_bound)
            {
                _bound = false;
                context.UnbindService(this);
            }
        }
        base.Dispose(disposing);
    }
}

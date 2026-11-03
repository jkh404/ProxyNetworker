using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.Net;
using Android.OS;
using Avalonia.Android;
using AndroidX.Activity.Result;
using AndroidX.Activity.Result.Contract;

namespace ProxyNetworker.App.Android;

[Activity(Label = "ProxyNetworker", MainLauncher = true, Exported = true,
    Theme = "@style/MyTheme.NoActionBar",
    ConfigurationChanges = ConfigChanges.Orientation | ConfigChanges.ScreenSize | ConfigChanges.UiMode)]
public sealed class MainActivity : AvaloniaMainActivity
{
    internal static MainActivity? Current { get; private set; }
    private ActivityResultLauncher? _vpnLauncher;
    private ActivityResultLauncher? _notificationLauncher;
    private TaskCompletionSource<bool>? _permission;
    private TaskCompletionSource<bool>? _notificationPermission;

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        Current = this;
        _vpnLauncher = RegisterForActivityResult(new ActivityResultContracts.StartActivityForResult(), new VpnResult(this));
        _notificationLauncher = RegisterForActivityResult(new ActivityResultContracts.RequestPermission(), new NotificationResult(this));
        base.OnCreate(savedInstanceState);
    }

    protected override void OnResume()
    {
        base.OnResume();
        Avalonia.Threading.Dispatcher.UIThread.Post(() => ProxyNetworker.App.App.ViewModel?.RefreshBackgroundSettings());
    }

    internal async Task RequestNotificationsAsync(CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsAndroidVersionAtLeast(33)) return;
        var source = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        RunOnUiThread(() =>
        {
            try
            {
                if (!OperatingSystem.IsAndroidVersionAtLeast(33))
                { source.TrySetResult(true); return; }
                if (CheckSelfPermission(global::Android.Manifest.Permission.PostNotifications) == Permission.Granted)
                { source.TrySetResult(true); return; }
                if (_notificationPermission is not null)
                { source.TrySetException(new InvalidOperationException("Notification authorization is already pending.")); return; }
                _notificationPermission = source;
                using var permission = new Java.Lang.String(global::Android.Manifest.Permission.PostNotifications);
                _notificationLauncher!.Launch(permission);
            }
            catch (Exception exception) { source.TrySetException(exception); }
        });
        using var registration = cancellationToken.Register(() => source.TrySetCanceled(cancellationToken));
        if (!await source.Task.ConfigureAwait(false))
            AndroidClientController.Shared.Log(new(DateTimeOffset.UtcNow, "Warning", "Notification permission was denied; the VPN can still be stopped from the app."));
    }

    internal async Task<bool> RequestVpnAsync(CancellationToken cancellationToken)
    {
        var source = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        RunOnUiThread(() =>
        {
            try
            {
                var preparation = VpnService.Prepare(this);
                if (preparation is null) { source.TrySetResult(true); return; }
                if (_permission is not null) { source.TrySetException(new InvalidOperationException("VPN authorization is already pending.")); return; }
                _permission = source;
                _vpnLauncher!.Launch(preparation);
            }
            catch (Exception exception) { source.TrySetException(exception); }
        });
        using var registration = cancellationToken.Register(() => source.TrySetCanceled(cancellationToken));
        return await source.Task.ConfigureAwait(false);
    }

    protected override void OnDestroy()
    {
        _permission?.TrySetCanceled(); _permission = null;
        _notificationPermission?.TrySetCanceled(); _notificationPermission = null;
        if (ReferenceEquals(Current, this)) Current = null;
        base.OnDestroy();
    }

    private sealed class VpnResult(MainActivity activity) : Java.Lang.Object, IActivityResultCallback
    {
        public void OnActivityResult(Java.Lang.Object? result)
        {
            activity._permission?.TrySetResult(result is ActivityResult value && value.ResultCode == (int)Result.Ok);
            activity._permission = null;
        }
    }
    private sealed class NotificationResult(MainActivity activity) : Java.Lang.Object, IActivityResultCallback
    {
        public void OnActivityResult(Java.Lang.Object? result)
        {
            activity._notificationPermission?.TrySetResult(result is Java.Lang.Boolean value && value.BooleanValue());
            activity._notificationPermission = null;
        }
    }
}

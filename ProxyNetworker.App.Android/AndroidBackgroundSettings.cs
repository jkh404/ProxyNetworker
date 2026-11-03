using Android.App;
using Android.Content;
using Android.OS;
using Android.Provider;
using ProxyNetworker.Client.Runtime;

namespace ProxyNetworker.App.Android;

internal static class AndroidBackgroundSettings
{
    internal static ClientBackgroundState GetState()
    {
        var context = global::Android.App.Application.Context;
        var power = (PowerManager)context.GetSystemService(Context.PowerService)!;
        var notifications = (NotificationManager)context.GetSystemService(Context.NotificationService)!;
        using var channel = notifications.GetNotificationChannel(NetworkClientService.ChannelId);
        return new(power.IsIgnoringBatteryOptimizations(context.PackageName!),
            notifications.AreNotificationsEnabled() && channel?.Importance != NotificationImportance.None);
    }

    internal static void OpenBatterySettings() => Open(new Intent(Settings.ActionIgnoreBatteryOptimizationSettings));

    internal static void OpenNotificationSettings()
    {
        var context = global::Android.App.Application.Context;
        Open(new Intent(Settings.ActionAppNotificationSettings).PutExtra(Settings.ExtraAppPackage, context.PackageName));
    }

    private static void Open(Intent intent)
    {
        var context = global::Android.App.Application.Context;
        try { StartActivity(intent); }
        catch (ActivityNotFoundException)
        {
            StartActivity(new Intent(Settings.ActionApplicationDetailsSettings,
                global::Android.Net.Uri.Parse("package:" + context.PackageName)));
        }
    }

    private static void StartActivity(Intent intent)
    {
        if (MainActivity.Current is { } activity) activity.StartActivity(intent);
        else global::Android.App.Application.Context.StartActivity(intent.AddFlags(ActivityFlags.NewTask));
    }
}

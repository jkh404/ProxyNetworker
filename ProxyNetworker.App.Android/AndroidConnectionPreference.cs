using Android.Content;

namespace ProxyNetworker.App.Android;

/// <summary>Persists connection intent only; credentials remain in the private configuration.</summary>
internal static class AndroidConnectionPreference
{
    private const string FileName = "network-lifecycle";
    private const string RequestedKey = "connection-requested";

    internal static bool Requested
    {
        get
        {
            using var preferences = global::Android.App.Application.Context.GetSharedPreferences(FileName, FileCreationMode.Private)!;
            return preferences.GetBoolean(RequestedKey, false);
        }
    }

    internal static bool SetRequested(bool requested)
    {
        using var preferences = global::Android.App.Application.Context.GetSharedPreferences(FileName, FileCreationMode.Private)!;
        using var editor = preferences.Edit()!;
        // Commit before starting/stopping, so a process death cannot reverse the user's latest intent.
        return editor.PutBoolean(RequestedKey, requested)!.Commit();
    }
}

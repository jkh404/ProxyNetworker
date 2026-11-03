using Android.Content;
using Android.Net;
using ProxyNetworker.Client.Runtime;
using SharedApp = ProxyNetworker.App.App;

namespace ProxyNetworker.App.Android;

/// <summary>Activity-independent controller; only the foreground service owns network resources.</summary>
internal sealed class AndroidClientController : IClientController, IClientBackgroundSettings
{
    internal static AndroidClientController Shared { get; } = new();
    private ClientState _state = ClientState.Stopped;
    public ClientState State => Volatile.Read(ref _state);
    public event Action<ClientState>? StateChanged;
    public event Action<ClientLog>? LogReceived;

    public async Task ConnectAsync(ProxyNetworkerClientConfig configuration, CancellationToken cancellationToken)
    {
        ClientConfigurationStore.Validate(configuration);
        var stopDeadline = DateTimeOffset.UtcNow.AddSeconds(5);
        while (NetworkClientService.Current is { IsStopping: true })
        {
            if (DateTimeOffset.UtcNow >= stopDeadline)
                throw new IOException("The previous VPN service has not stopped yet.");
            await Task.Delay(50, cancellationToken).ConfigureAwait(false);
        }
        if (configuration.VirtualNetwork is { Enabled: true })
        {
            var activity = MainActivity.Current ?? throw new InvalidOperationException("Open the app before requesting VPN authorization.");
            if (!await activity.RequestVpnAsync(cancellationToken).ConfigureAwait(false))
                throw new InvalidOperationException("VPN authorization was not granted.");
        }
        if (MainActivity.Current is { } current)
            await current.RequestNotificationsAsync(cancellationToken).ConfigureAwait(false);
        await new ClientConfigurationStore(SharedApp.ConfigurationPath).SaveAsync(configuration, cancellationToken).ConfigureAwait(false);
        Publish(ClientState.Stopped with { State = ClientConnectionState.Starting });
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!AndroidConnectionPreference.SetRequested(true))
                throw new IOException("Could not save the background connection request.");
            var context = global::Android.App.Application.Context;
            context.StartForegroundService(new Intent(context, typeof(NetworkClientService)).SetAction(NetworkClientService.StartAction));
        }
        catch
        {
            AndroidConnectionPreference.SetRequested(false);
            Publish(ClientState.Stopped);
            throw;
        }
    }

    public async Task DisconnectAsync(CancellationToken cancellationToken)
    {
        if (!AndroidConnectionPreference.SetRequested(false))
            Log(new(DateTimeOffset.UtcNow, "Warning", "Could not persist the stopped background connection state."));
        var service = NetworkClientService.Current;
        if (service is not null) await service.StopNetworkAsync().WaitAsync(cancellationToken).ConfigureAwait(false);
        else if (State.State == ClientConnectionState.Starting)
        {
            // The start intent may still be queued before OnCreate has published Current.
            var context = global::Android.App.Application.Context;
            context.StartService(new Intent(context, typeof(NetworkClientService)).SetAction(NetworkClientService.StopAction));
        }
        else Publish(ClientState.Stopped);
    }
    internal void Publish(ClientState state) { Volatile.Write(ref _state, state); StateChanged?.Invoke(state); }
    internal void Log(ClientLog log) => LogReceived?.Invoke(log);
    public ClientBackgroundState GetBackgroundState() => AndroidBackgroundSettings.GetState();
    public void OpenBatterySettings() => AndroidBackgroundSettings.OpenBatterySettings();
    public void OpenNotificationSettings() => AndroidBackgroundSettings.OpenNotificationSettings();
    public async ValueTask DisposeAsync() => await DisconnectAsync(CancellationToken.None).ConfigureAwait(false);
}

using ProxyNetworker.Core;

namespace ProxyNetworker.Client.Runtime;

/// <summary>Lifecycle of the local network worker, not a remote reachability guarantee.</summary>
public enum ClientConnectionState { Stopped, Starting, Running, Reconnecting, Stopping, Failed }

/// <summary>A bounded, credential-free view of a running client.</summary>
public sealed record ClientState(
    ClientConnectionState State,
    string? VirtualAddress,
    string? InterfaceName,
    string? Transport,
    bool EncryptionEnabled,
    long BytesSent,
    long BytesReceived,
    DateTimeOffset? StartedAt,
    string? Error,
    ClientPeerState[] Peers,
    ClientPortState[] PortTunnels)
{
    public static ClientState Stopped { get; } = new(
        ClientConnectionState.Stopped, null, null, null, true, 0, 0, null, null, [], []);
}

/// <summary>A measured peer path reported by the network core.</summary>
public sealed record ClientPeerState(string Address, string Endpoint, string Path, double? RttMilliseconds);

/// <summary>The authenticated readiness of a configured port tunnel.</summary>
public sealed record ClientPortState(string Name, bool Ready, string? Protocol, int? PublicPort);

/// <summary>A sanitized diagnostic entry safe to send over the local control channel.</summary>
public sealed record ClientLog(DateTimeOffset Timestamp, string Level, string Message);

/// <summary>Platform-specific device creation and outer transport protection.</summary>
public interface IClientPlatform
{
    /// <summary>Creates an owned device. Cancellation or failure must not retain an interface.</summary>
    Task<ITunDevice> CreateTunAsync(TunDeviceOptions options, Action<ProxyNetworkerLogEntry> logger, CancellationToken cancellationToken);

    /// <summary>Protects a newly created UDP socket before any tunnel traffic.</summary>
    void ConfigureTransportSocket(System.Net.Sockets.Socket socket);

    /// <summary>Whether this host permits the existing QUIC transport.</summary>
    bool SupportsQuic { get; }
}

/// <summary>The desktop host for the existing platform TUN factory.</summary>
public sealed class DesktopClientPlatform : IClientPlatform
{
    public bool SupportsQuic => !OperatingSystem.IsAndroid();
    public Task<ITunDevice> CreateTunAsync(TunDeviceOptions options, Action<ProxyNetworkerLogEntry> logger, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(TunDeviceFactory.Create(options, logger));
    }
    public void ConfigureTransportSocket(System.Net.Sockets.Socket socket) { }
}

/// <summary>A platform controller consumed by the shared Avalonia view model.</summary>
public interface IClientController : IAsyncDisposable
{
    ClientState State { get; }
    event Action<ClientState>? StateChanged;
    event Action<ClientLog>? LogReceived;
    Task ConnectAsync(ProxyNetworkerClientConfig configuration, CancellationToken cancellationToken);
    Task DisconnectAsync(CancellationToken cancellationToken);
}

/// <summary>System-managed background and notification policy reported by a mobile host.</summary>
public sealed record ClientBackgroundState(bool BatteryOptimizationExempt, bool NotificationsEnabled);

/// <summary>Optional access to system settings, opened only after an explicit user action.</summary>
public interface IClientBackgroundSettings
{
    ClientBackgroundState GetBackgroundState();
    void OpenBatterySettings();
    void OpenNotificationSettings();
}

using System.Net;

namespace ProxyNetworker.Core;

public sealed record VirtualNetworkTopologySnapshot(
    string TransportProtocol,
    DateTimeOffset CapturedAt,
    IReadOnlyList<VirtualNetworkTopologyNodeSnapshot> Nodes,
    IReadOnlyList<VirtualNetworkTopologyEdgeSnapshot> Edges);

public sealed record VirtualNetworkTopologyNodeSnapshot(
    IPAddress VirtualAddress,
    string Role,
    string NodeName,
    string MachineName,
    string OsDescription,
    string OsArchitecture,
    string ProcessArchitecture,
    string ClientVersion,
    bool IsOnline,
    long BytesSent,
    long BytesReceived,
    DateTimeOffset? ConnectedAt,
    DateTimeOffset? LastSeenAt);

public sealed record VirtualNetworkTopologyEdgeSnapshot(
    IPAddress SourceAddress,
    IPAddress DestinationAddress,
    string ConnectionType,
    string TransportProtocol,
    double? RttMilliseconds,
    double? JitterMilliseconds,
    double? LossPercent,
    int? CostMilliseconds,
    long BytesSent,
    long BytesReceived,
    double BytesPerSecondSent,
    double BytesPerSecondReceived,
    DateTimeOffset LastSeenAt);

public static class VirtualNetworkTopologyConnectionTypes
{
    public const string Relay = "relay";
    public const string Direct = "direct";
}

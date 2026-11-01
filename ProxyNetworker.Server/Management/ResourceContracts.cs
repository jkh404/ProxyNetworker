namespace ProxyNetworker.Server.Management;

public sealed class CreateVirtualNetworkDefinitionRequest
{
    public string Name { get; set; } = string.Empty;

    public string GatewayAddress { get; set; } = string.Empty;

    public int PrefixLength { get; set; }

    public int ListenPort { get; set; }

    public int Mtu { get; set; }

    public string TransportProtocol { get; set; } = ProxyNetworker.Core.VirtualNetworkTransportProtocols.Udp;

    public bool EncryptionEnabled { get; set; } = true;
}

public sealed record VirtualNetworkDefinitionResponse(
    string Id,
    string OwnerUserId,
    string Name,
    string GatewayAddress,
    int PrefixLength,
    int ListenPort,
    int Mtu,
    string TransportProtocol,
    bool EncryptionEnabled,
    bool IsEnabled,
    DateTimeOffset CreatedAt);

public sealed class SetVirtualNetworkEnabledRequest
{
    public bool Enabled { get; set; }
}

public sealed class SetVirtualNetworkProtocolRequest
{
    public string TransportProtocol { get; set; } = ProxyNetworker.Core.VirtualNetworkTransportProtocols.Udp;
}

public sealed class SetVirtualNetworkEncryptionRequest
{
    public bool EncryptionEnabled { get; set; } = true;
}

public sealed class UpdateVirtualNetworkTokenAddressRequest
{
    public string VirtualAddress { get; set; } = string.Empty;
}

public sealed class BanVirtualNetworkNodeRequest
{
    public string VirtualAddress { get; set; } = string.Empty;

    public string Reason { get; set; } = string.Empty;
}

public sealed class CreatePortTunnelDefinitionRequest
{
    public string Name { get; set; } = string.Empty;

    public string Protocol { get; set; } = "tcp";

    public string PrivateHost { get; set; } = "127.0.0.1";

    public int PrivatePort { get; set; }
}

public sealed record PortTunnelDefinitionResponse(
    string Id,
    string OwnerUserId,
    string Name,
    string Protocol,
    string PrivateHost,
    int PrivatePort,
    int PublicPort,
    long BandwidthLimitBytes,
    long MaxTrafficSpeedBytesPerSecond,
    DateTimeOffset CreatedAt);

public sealed class CreateAccessTokenRequest
{
    public string TokenType { get; set; } = Data.AccessTokenTypes.Permanent;

    public DateTimeOffset? ValidFrom { get; set; }

    public DateTimeOffset? ValidUntil { get; set; }
}

public sealed record AccessTokenResponse(
    string Id,
    string ScopeKind,
    string ResourceId,
    string TokenPreview,
    string TokenType,
    DateTimeOffset? ValidFrom,
    DateTimeOffset? ValidUntil,
    bool IsConsumed,
    bool HasStoredValue,
    string AssignedVirtualAddress,
    DateTimeOffset CreatedAt);

public sealed record VirtualNetworkTokenAddressResponse(
    string TokenId,
    string VirtualAddress);

public sealed record CreatedAccessTokenResponse(
    AccessTokenResponse Token,
    string PlainTextToken);

public sealed record AccessTokenValueResponse(string PlainTextToken);

public sealed record VirtualNetworkOnlineNodeResponse(
    string VirtualAddress,
    string NodeName,
    string MachineName,
    string OsDescription,
    string OsArchitecture,
    string ProcessArchitecture,
    string ClientVersion,
    string PublicEndpoint,
    string[] LocalEndpoints,
    string TokenId,
    string TokenPreview,
    DateTimeOffset ConnectedAt,
    DateTimeOffset LastSeenAt);

public sealed record VirtualNetworkSessionResponse(
    string Id,
    string VirtualAddress,
    string NodeName,
    string MachineName,
    string OsDescription,
    string OsArchitecture,
    string ProcessArchitecture,
    string ClientVersion,
    string PublicEndpoint,
    string[] LocalEndpoints,
    string TokenId,
    string TokenPreview,
    DateTimeOffset ConnectedAt,
    DateTimeOffset LastSeenAt,
    DateTimeOffset? DisconnectedAt);

public sealed record VirtualNetworkStatusResponse(
    bool IsEnabled,
    int OnlineNodeCount,
    VirtualNetworkOnlineNodeResponse[] OnlineNodes,
    AccessTokenResponse[] Tokens,
    VirtualNetworkSessionResponse[] RecentSessions,
    VirtualNetworkNodeBanResponse[] BannedNodes);

public sealed record VirtualNetworkTopologyNodeResponse(
    string VirtualAddress,
    string Role,
    string NodeName,
    string MachineName,
    string OsDescription,
    string OsArchitecture,
    string ProcessArchitecture,
    string ClientVersion,
    bool IsOnline,
    string TokenId,
    string TokenPreview,
    long BytesSent,
    long BytesReceived,
    DateTimeOffset? ConnectedAt,
    DateTimeOffset? LastSeenAt);

public sealed record VirtualNetworkTopologyEdgeResponse(
    string SourceAddress,
    string DestinationAddress,
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

public sealed record VirtualNetworkTopologyResponse(
    bool IsEnabled,
    string GatewayAddress,
    string TransportProtocol,
    DateTimeOffset CapturedAt,
    VirtualNetworkTopologyNodeResponse[] Nodes,
    VirtualNetworkTopologyEdgeResponse[] Edges);

public sealed record VirtualNetworkNodeBanResponse(
    string Id,
    string VirtualAddress,
    string TokenId,
    string NodeName,
    string Reason,
    DateTimeOffset CreatedAt);

public sealed class UpdateSystemSettingsRequest
{
    public int PublicPortRangeStart { get; set; }

    public int PublicPortRangeEnd { get; set; }

    public long MaxBandwidthLimitBytes { get; set; }

    public long MaxTrafficSpeedBytesPerSecond { get; set; }
}

public sealed record SystemSettingsResponse(
    int PublicPortRangeStart,
    int PublicPortRangeEnd,
    long MaxBandwidthLimitBytes,
    long MaxTrafficSpeedBytesPerSecond,
    DateTimeOffset UpdatedAt);

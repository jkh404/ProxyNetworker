namespace ProxyNetworker.Server.Management;

public sealed record ServerInfoResponse(
    string Name,
    string Role,
    string[] Capabilities);

public sealed record HealthResponse(
    string Status,
    DateTimeOffset CheckedAt);

public sealed record ErrorResponse(string Message);

public sealed record TunnelSummary(
    string Id,
    string Name,
    string Kind,
    string? Protocol,
    string PublicEndpoint,
    string? PrivateService,
    DateTimeOffset StartedAt,
    string Status);

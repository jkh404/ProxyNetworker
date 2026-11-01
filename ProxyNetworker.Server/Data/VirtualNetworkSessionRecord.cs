namespace ProxyNetworker.Server.Data;

public sealed class VirtualNetworkSessionRecord
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    public string NetworkId { get; set; } = string.Empty;

    public string VirtualAddress { get; set; } = string.Empty;

    public string TokenId { get; set; } = string.Empty;

    public string TokenPreview { get; set; } = string.Empty;

    public string NodeName { get; set; } = string.Empty;

    public string MachineName { get; set; } = string.Empty;

    public string OsDescription { get; set; } = string.Empty;

    public string OsArchitecture { get; set; } = string.Empty;

    public string ProcessArchitecture { get; set; } = string.Empty;

    public string ClientVersion { get; set; } = string.Empty;

    public string PublicEndpoint { get; set; } = string.Empty;

    public string LocalEndpoints { get; set; } = string.Empty;

    public long ConnectedAtUnixMs { get; set; }

    public long LastSeenAtUnixMs { get; set; }

    public long? DisconnectedAtUnixMs { get; set; }
}

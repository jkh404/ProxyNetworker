namespace ProxyNetworker.Server.Data;

public sealed class VirtualNetworkNodeBanRecord
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    public string NetworkId { get; set; } = string.Empty;

    public string VirtualAddress { get; set; } = string.Empty;

    public string TokenId { get; set; } = string.Empty;

    public string NodeName { get; set; } = string.Empty;

    public string Reason { get; set; } = string.Empty;

    public long CreatedAtUnixMs { get; set; } = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
}

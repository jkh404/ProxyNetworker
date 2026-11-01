using System.Text.Json.Serialization;

namespace ProxyNetworker.Core.PortTunnels.Remote;

public sealed class RemotePortTunnelConfig
{
    [JsonPropertyName("TunnelId")]
    public string TunnelId { get; set; } = string.Empty;

    [JsonPropertyName("Name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("Protocol")]
    public string Protocol { get; set; } = "tcp";

    [JsonPropertyName("PrivateHost")]
    public string PrivateHost { get; set; } = "127.0.0.1";

    [JsonPropertyName("PrivatePort")]
    public int PrivatePort { get; set; }

    [JsonPropertyName("PublicPort")]
    public int PublicPort { get; set; }
}

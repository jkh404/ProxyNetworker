using System.Text.Json.Serialization;
using ProxyNetworker.Core.PortTunnels.Remote;

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    PropertyNameCaseInsensitive = true,
    WriteIndented = false)]
[JsonSerializable(typeof(ProxyNetworkerClientConfig))]
[JsonSerializable(typeof(LoginRequest))]
[JsonSerializable(typeof(ChangePasswordRequest))]
[JsonSerializable(typeof(ErrorResponse))]
[JsonSerializable(typeof(AccountResponse))]
[JsonSerializable(typeof(CreatePortTunnelDefinitionRequest))]
[JsonSerializable(typeof(PortTunnelDefinitionResponse))]
[JsonSerializable(typeof(CreateVirtualNetworkDefinitionRequest))]
[JsonSerializable(typeof(VirtualNetworkDefinitionResponse))]
[JsonSerializable(typeof(CreateAccessTokenRequest))]
[JsonSerializable(typeof(AccessTokenResponse))]
[JsonSerializable(typeof(CreatedAccessTokenResponse))]
[JsonSerializable(typeof(VirtualNetworkClientConfigResponse))]
[JsonSerializable(typeof(RemotePortTunnelConfig))]
internal sealed partial class ClientJsonContext : JsonSerializerContext
{
}

using System.ComponentModel.DataAnnotations;
using System.Net;
using Microsoft.Extensions.Options;

namespace ProxyNetworker.Server.Management;

/// <summary>
/// Default network values used when a new virtual network is created.
/// Changing these values does not update resources that are already persisted in the database.
/// </summary>
public sealed class NetworkDefaultsOptions
{
    public const string Section = "NetworkDefaults";

    /// <summary>
    /// Local IP address used by port tunnel and virtual network listeners allocated from
    /// the public port range. This is independent from the management endpoint configured by urls.
    /// Use 0.0.0.0 to listen on all IPv4 interfaces, or a specific interface address to restrict it.
    /// </summary>
    [Required]
    public string PublicListenAddress { get; init; } = "0.0.0.0";

    /// <summary>
    /// Default IPv4 gateway assigned to a new virtual network when no gateway is provided.
    /// Existing virtual network definitions keep their stored gateway.
    /// </summary>
    [Required]
    public string VirtualNetworkGatewayAddress { get; init; } = string.Empty;

    /// <summary>
    /// Default IPv4 prefix length for a new virtual network. The supported range is 1-30,
    /// leaving usable host addresses for the gateway and clients.
    /// </summary>
    [Range(1, 30)]
    public int VirtualNetworkPrefixLength { get; init; }

    /// <summary>
    /// Default virtual network MTU for a new virtual network. The supported range is 576-65535;
    /// 1400 is a conservative value for encapsulated or routed links.
    /// </summary>
    [Range(576, 65535)]
    public int VirtualNetworkMtu { get; init; }
}

public sealed class NetworkDefaultsOptionsValidator : IValidateOptions<NetworkDefaultsOptions>
{
    public ValidateOptionsResult Validate(string? name, NetworkDefaultsOptions options)
    {
        if (!IPAddress.TryParse(options.PublicListenAddress, out var publicListenAddress) ||
            publicListenAddress.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork)
        {
            return ValidateOptionsResult.Fail("NetworkDefaults:PublicListenAddress must be a valid IPv4 address.");
        }

        if (!IPAddress.TryParse(options.VirtualNetworkGatewayAddress, out var gatewayAddress) ||
            gatewayAddress.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork)
        {
            return ValidateOptionsResult.Fail("NetworkDefaults:VirtualNetworkGatewayAddress must be a valid IPv4 address.");
        }

        return ValidateOptionsResult.Success;
    }
}

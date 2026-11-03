using System.Net;
using System.Net.Sockets;

namespace ProxyNetworker.Core;

public class VirtualNetworkTunnelOptions
{
    public IPEndPoint BindEndPoint { get; set; } = new(IPAddress.Any, 0);

    public IPEndPoint? RemoteEndPoint { get; set; }

    public IPAddress NodeAddress { get; set; } = IPAddress.Parse("10.66.0.1");

    public int PrefixLength { get; set; } = 24;

    public string NodeName { get; set; } = Environment.MachineName;

    public byte[] AuthenticationKey { get; set; } = Array.Empty<byte>();

    public bool EncryptionEnabled { get; set; } = true;

    public bool EnableDirectPaths { get; set; } = true;

    /// <summary>
    /// Gets or sets whether healthy private direct candidates receive a bounded preference
    /// and small score changes retain the current direct path. Enabled by default.
    /// </summary>
    public bool EnableStableDirectPathSelection { get; set; } = true;

    public IReadOnlyCollection<IPEndPoint> AdditionalCandidateEndPoints { get; set; } = Array.Empty<IPEndPoint>();

    public IReadOnlyCollection<IPAddress> DirectPathExcludedAddresses { get; set; } = Array.Empty<IPAddress>();

    /// <summary>
    /// Gets or sets the permitted local and remote direct candidate IPv4 addresses.
    /// An empty collection permits all candidate addresses and does not restrict the relay endpoint.
    /// </summary>
    public IReadOnlyCollection<IPAddress> AllowedDirectCandidateAddresses { get; set; } = Array.Empty<IPAddress>();

    public bool IncludeLocalInterfaceCandidates { get; set; } = true;

    /// <summary>
    /// Gets or sets a startup-only callback applied before any transport traffic.
    /// Android hosts use this to protect the outer socket from their VPN.
    /// </summary>
    public Action<Socket>? ConfigureTransportSocket { get; set; }

    public int MaxPacketSize { get; set; } = 65535;

    public TimeSpan HeartbeatInterval { get; set; } = TimeSpan.FromSeconds(5);

    public TimeSpan PeerTimeout { get; set; } = TimeSpan.FromSeconds(20);

    public TimeSpan DirectPathFailureCooldown { get; set; } = TimeSpan.FromSeconds(30);

    public void Validate()
    {
        if (BindEndPoint is null)
        {
            throw new ArgumentNullException(nameof(BindEndPoint));
        }

        if (MaxPacketSize <= 0 || MaxPacketSize > 65535)
        {
            throw new ArgumentOutOfRangeException(nameof(MaxPacketSize), MaxPacketSize, "UDP packet size must be between 1 and 65535.");
        }

        if (NodeAddress.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork)
        {
            throw new PlatformNotSupportedException("Virtual Network currently supports IPv4 only.");
        }

        if (PrefixLength < 0 || PrefixLength > 32)
        {
            throw new ArgumentOutOfRangeException(nameof(PrefixLength), PrefixLength, "IPv4 prefix length must be between 0 and 32.");
        }

        if (HeartbeatInterval < TimeSpan.FromSeconds(1))
        {
            throw new ArgumentOutOfRangeException(nameof(HeartbeatInterval), HeartbeatInterval, "Heartbeat interval must be at least 1 second.");
        }

        if (PeerTimeout <= HeartbeatInterval)
        {
            throw new ArgumentOutOfRangeException(nameof(PeerTimeout), PeerTimeout, "Peer timeout must be greater than the heartbeat interval.");
        }

        if (DirectPathFailureCooldown < TimeSpan.FromSeconds(1))
        {
            throw new ArgumentOutOfRangeException(nameof(DirectPathFailureCooldown), DirectPathFailureCooldown, "Direct path failure cooldown must be at least one second.");
        }

        if (AuthenticationKey is not { Length: 32 })
        {
            throw new ArgumentException("Virtual Network authentication key must be 32 bytes.", nameof(AuthenticationKey));
        }

        foreach (var endpoint in AdditionalCandidateEndPoints)
        {
            if (endpoint.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork ||
                endpoint.Port is <= 0 or > ushort.MaxValue)
            {
                throw new ArgumentException("Additional candidate endpoints must be valid IPv4 endpoints.", nameof(AdditionalCandidateEndPoints));
            }
        }

        foreach (var address in DirectPathExcludedAddresses)
        {
            if (address.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork)
            {
                throw new ArgumentException("Direct path exclusions must be IPv4 addresses.", nameof(DirectPathExcludedAddresses));
            }
        }

        foreach (var address in AllowedDirectCandidateAddresses)
        {
            if (address.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork)
            {
                throw new ArgumentException("Direct candidate allowlist entries must be IPv4 addresses.", nameof(AllowedDirectCandidateAddresses));
            }
        }
    }
}

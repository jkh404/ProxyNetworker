using System.Net;

namespace ProxyNetworker.Core;

public interface IVirtualNetworkRelay : IDisposable
{
    int PeerCount { get; }

    IReadOnlyList<VirtualNetworkPeerSnapshot> GetPeerSnapshots();

    VirtualNetworkTopologySnapshot GetTopologySnapshot();

    Task StartAsync(CancellationToken cancellationToken = default);

    void SetPeerBanned(IPAddress virtualAddress, bool isBanned);
}

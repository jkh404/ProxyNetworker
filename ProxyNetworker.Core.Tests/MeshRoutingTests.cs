using System.Net;
using ProxyNetworker.Core;

namespace ProxyNetworker.Core.Tests;

public sealed class MeshRoutingTests
{
    [Fact]
    public void RouteTableSelectsMultiHopThenFasterDirectPath()
    {
        var local = IPAddress.Parse("10.66.0.2");
        var relay = IPAddress.Parse("10.66.0.3");
        var destination = IPAddress.Parse("10.66.0.4");
        var relayAdvertisement = new VirtualNetworkMeshAdvertisement(
            1,
            [VirtualNetworkMeshLink.Create(destination, 20, 1, 0)],
            DateTimeOffset.UtcNow);
        var advertisements = new Dictionary<string, (IPAddress Origin, VirtualNetworkMeshAdvertisement Advertisement)>
        {
            [relay.ToString()] = (relay, relayAdvertisement)
        };

        var multiHop = VirtualNetworkMeshRouteTable.Build(
            local,
            [VirtualNetworkMeshLink.Create(relay, 10, 1, 0)],
            advertisements,
            DateTimeOffset.UtcNow,
            TimeSpan.FromSeconds(30));

        Assert.True(multiHop.TryGetRoute(destination, out var multiHopRoute));
        Assert.Equal(relay, multiHopRoute.NextHop);
        Assert.Equal(2, multiHopRoute.HopCount);
        Assert.Equal("10.66.0.2 -> 10.66.0.3 -> 10.66.0.4", multiHopRoute.Path);

        var direct = VirtualNetworkMeshRouteTable.Build(
            local,
            [
                VirtualNetworkMeshLink.Create(relay, 30, 1, 0),
                VirtualNetworkMeshLink.Create(destination, 5, 0, 0)
            ],
            advertisements,
            DateTimeOffset.UtcNow,
            TimeSpan.FromSeconds(30));

        Assert.True(direct.TryGetRoute(destination, out var directRoute));
        Assert.Equal(destination, directRoute.NextHop);
        Assert.Equal(1, directRoute.HopCount);
    }

    [Fact]
    public void LinkStateAdvertisementRoundTrips()
    {
        var advertisement = new VirtualNetworkMeshAdvertisement(
            42,
            [
                VirtualNetworkMeshLink.Create(IPAddress.Parse("10.66.0.3"), 12, 2, 100),
                VirtualNetworkMeshLink.Create(IPAddress.Parse("10.66.0.4"), 25, 4, 250)
            ],
            DateTimeOffset.UtcNow);

        Assert.True(VirtualNetworkMeshAdvertisement.TryDecode(advertisement.Encode(), out var decoded));
        Assert.Equal(advertisement.Sequence, decoded.Sequence);
        Assert.Equal(2, decoded.Links.Count);
        Assert.Equal(IPAddress.Parse("10.66.0.3"), decoded.Links[0].VirtualAddress);
        Assert.Equal(12, decoded.Links[0].RttMilliseconds);
    }

    [Fact]
    public void MeshPacketRejectsForwardingPastHopLimit()
    {
        var payload = VirtualNetworkMeshPacket.Create(IPAddress.Parse("10.66.0.4"), [0x45, 0, 0, 20]);
        Assert.True(VirtualNetworkMeshPacket.TryParse(payload, out var packet));

        for (var hop = 0; hop < VirtualNetworkMeshPacket.DefaultHopLimit - 1; hop++)
        {
            Assert.True(packet.TryCreateForwarded(out var forwarded));
            Assert.True(VirtualNetworkMeshPacket.TryParse(forwarded, out packet));
        }

        Assert.False(packet.TryCreateForwarded(out _));
    }
}

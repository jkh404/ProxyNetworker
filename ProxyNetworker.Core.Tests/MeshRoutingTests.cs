using System.Net;
using System.Runtime.InteropServices;
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
    public void MeshTrafficReportRoundTrips()
    {
        var report = new VirtualNetworkMeshTrafficReport(
            73,
            [
                new VirtualNetworkMeshTraffic(IPAddress.Parse("10.66.0.3"), 123456, 654321),
                new VirtualNetworkMeshTraffic(IPAddress.Parse("10.66.0.4"), 42, 24)
            ]);

        Assert.True(VirtualNetworkMeshTrafficReport.TryDecode(report.Encode(), out var decoded));
        Assert.Equal(73, decoded.Sequence);
        Assert.Equal(2, decoded.Links.Count);
        Assert.Equal(IPAddress.Parse("10.66.0.3"), decoded.Links[0].VirtualAddress);
        Assert.Equal(123456, decoded.Links[0].BytesSent);
        Assert.Equal(654321, decoded.Links[0].BytesReceived);
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

    [Fact]
    public void MeshPacketParserRetainsTheOriginalBufferSlice()
    {
        var destination = IPAddress.Parse("10.66.0.4");
        var expected = new byte[1400];
        Random.Shared.NextBytes(expected);
        var payload = VirtualNetworkMeshPacket.Create(destination, expected);
        var buffer = new byte[payload.Length + 14];
        payload.CopyTo(buffer, 7);

        Assert.True(VirtualNetworkMeshPacket.TryParse(buffer.AsMemory(7, payload.Length), out var packet));

        Assert.True(MemoryMarshal.TryGetArray(packet.Payload, out var segment));
        Assert.Same(buffer, segment.Array);
        Assert.Equal(15, segment.Offset);
        Assert.Equal(expected.Length, segment.Count);
        Assert.Equal(expected, packet.Payload.ToArray());
    }

    [Fact]
    public void MeshPayloadCanBeWrittenIntoDirtyMemoryWithoutChangingTheWireFormat()
    {
        var destination = IPAddress.Parse("10.66.0.4");
        byte[] packet = [0x45, 0, 0, 20];
        var expected = VirtualNetworkMeshPacket.Create(destination, packet);
        var payload = new byte[VirtualNetworkMeshPacket.GetPayloadLength(packet.Length)];
        payload.AsSpan().Fill(0xcc);

        VirtualNetworkMeshPacket.CreateInto(destination, packet, payload);

        Assert.Equal(expected, payload);
        Assert.Equal(0, payload[3]);
    }

    [Fact]
    public void ForwardingDoesNotMutateTheReceivedMeshBuffer()
    {
        var payload = VirtualNetworkMeshPacket.Create(IPAddress.Parse("10.66.0.4"), [0x45, 0, 0, 20]);
        var original = payload.ToArray();
        Assert.True(VirtualNetworkMeshPacket.TryParse(payload, out var packet));

        Assert.True(packet.TryCreateForwarded(out var forwarded));

        Assert.Equal(original, payload);
        Assert.True(VirtualNetworkMeshPacket.TryParse(forwarded, out var next));
        Assert.Equal(1, next.HopCount);
        Assert.Equal(packet.Payload.ToArray(), next.Payload.ToArray());
    }

    [Theory]
    [InlineData(2, 0, 8)]
    [InlineData(1, 0, 0)]
    [InlineData(1, 0, 17)]
    [InlineData(1, 8, 8)]
    public void MeshPacketParserRejectsInvalidMetadata(byte version, byte hopCount, byte hopLimit)
    {
        var payload = VirtualNetworkMeshPacket.Create(IPAddress.Parse("10.66.0.4"), [0x45]);
        payload[0] = version;
        payload[1] = hopCount;
        payload[2] = hopLimit;

        Assert.False(VirtualNetworkMeshPacket.TryParse(payload, out _));
    }
}

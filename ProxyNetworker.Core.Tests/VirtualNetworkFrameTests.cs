using System.Security.Cryptography;
using System.Runtime.InteropServices;
using ProxyNetworker.Core;

namespace ProxyNetworker.Core.Tests;

public sealed class VirtualNetworkFrameTests
{
    [Theory]
    [InlineData(true, 2)]
    [InlineData(false, 3)]
    public void FrameRoundTripsInSelectedAuthenticationMode(bool encryptionEnabled, byte expectedVersion)
    {
        var key = RandomNumberGenerator.GetBytes(32);
        var payload = Enumerable.Range(0, 512).Select(static value => (byte)value).ToArray();

        var datagram = VirtualNetworkFrame.Create(
            VirtualNetworkFrameType.Packet,
            System.Net.IPAddress.Parse("10.77.0.42"),
            payload,
            key,
            encryptionEnabled);

        Assert.Equal(expectedVersion, datagram[4]);
        Assert.True(VirtualNetworkFrame.TryParse(datagram, key, encryptionEnabled, out var parsed));
        Assert.Equal(VirtualNetworkFrameType.Packet, parsed.Type);
        Assert.Equal("10.77.0.42", parsed.VirtualAddress.ToString());
        Assert.Equal(payload, parsed.Payload.ToArray());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void FrameRejectsPayloadAndAuthenticationTagTampering(bool encryptionEnabled)
    {
        var key = RandomNumberGenerator.GetBytes(32);
        var payload = RandomNumberGenerator.GetBytes(256);
        var datagram = VirtualNetworkFrame.Create(
            VirtualNetworkFrameType.Packet,
            System.Net.IPAddress.Parse("10.77.0.43"),
            payload,
            key,
            encryptionEnabled);

        datagram[^1] ^= 0x40;
        Assert.False(VirtualNetworkFrame.TryParse(datagram, key, encryptionEnabled, out _));

        datagram = VirtualNetworkFrame.Create(
            VirtualNetworkFrameType.Packet,
            System.Net.IPAddress.Parse("10.77.0.43"),
            payload,
            key,
            encryptionEnabled);
        datagram[HeaderProbeOffset(encryptionEnabled)] ^= 0x01;
        Assert.False(VirtualNetworkFrame.TryParse(datagram, key, encryptionEnabled, out _));
    }

    [Fact]
    public void FrameRequiresTheConfiguredAuthenticationMode()
    {
        var key = RandomNumberGenerator.GetBytes(32);
        var payload = RandomNumberGenerator.GetBytes(32);
        var encrypted = VirtualNetworkFrame.Create(
            VirtualNetworkFrameType.Packet,
            System.Net.IPAddress.Parse("10.77.0.44"),
            payload,
            key,
            encryptionEnabled: true);

        Assert.False(VirtualNetworkFrame.TryParse(encrypted, key, encryptionEnabled: false, out _));
    }

    [Fact]
    public void NonceGeneratorProducesUniqueValuesAcrossThreads()
    {
        var key = RandomNumberGenerator.GetBytes(32);
        var nonces = new System.Collections.Concurrent.ConcurrentDictionary<string, byte>(StringComparer.Ordinal);
        var address = System.Net.IPAddress.Parse("10.77.0.45");

        Parallel.For(0, 8, _ =>
        {
            for (var index = 0; index < 1000; index++)
            {
                var frame = VirtualNetworkFrame.Create(
                    VirtualNetworkFrameType.Packet,
                    address,
                    [1],
                    key,
                    encryptionEnabled: false);
                var nonce = Convert.ToHexString(frame.AsSpan(24, 12));
                Assert.True(nonces.TryAdd(nonce, 0), $"Duplicate nonce generated: {nonce}");
            }
        });

        Assert.Equal(8000, nonces.Count);
    }

    [Theory]
    [InlineData(true, 0)]
    [InlineData(true, 1)]
    [InlineData(true, 1400)]
    [InlineData(false, 0)]
    [InlineData(false, 1)]
    [InlineData(false, 1400)]
    public void PayloadCanBeConstructedInsideTheFinalFrame(bool encryptionEnabled, int length)
    {
        var key = RandomNumberGenerator.GetBytes(32);
        var expected = RandomNumberGenerator.GetBytes(length);
        var address = System.Net.IPAddress.Parse("10.77.0.46");
        var datagram = new byte[VirtualNetworkFrame.GetFrameLength(length)];
        datagram.AsSpan().Fill(0xcc);
        var payload = VirtualNetworkFrame.GetPayloadBuffer(datagram, length, encryptionEnabled);
        expected.CopyTo(payload);

        VirtualNetworkFrame.CreateInto(
            VirtualNetworkFrameType.Packet, address, payload, key, encryptionEnabled, datagram);

        Assert.True(VirtualNetworkFrame.TryParse(datagram, key, encryptionEnabled, out var parsed));
        Assert.Equal(expected, parsed.Payload.ToArray());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void InPlaceMeshFrameRoundTripsAndRejectsTampering(bool encryptionEnabled)
    {
        var key = RandomNumberGenerator.GetBytes(32);
        var source = System.Net.IPAddress.Parse("10.77.0.46");
        var destination = System.Net.IPAddress.Parse("10.77.0.47");
        var expected = RandomNumberGenerator.GetBytes(1400);
        var payloadLength = VirtualNetworkMeshPacket.GetPayloadLength(expected.Length);
        var datagram = new byte[VirtualNetworkFrame.GetFrameLength(payloadLength)];
        datagram.AsSpan().Fill(0xcc);
        var payload = VirtualNetworkFrame.GetPayloadBuffer(datagram, payloadLength, encryptionEnabled);
        VirtualNetworkMeshPacket.CreateInto(destination, expected, payload, 2, 8);
        VirtualNetworkFrame.CreateInto(
            VirtualNetworkFrameType.MeshPacket, source, payload, key, encryptionEnabled, datagram);
        var valid = datagram.ToArray();
        var headerTampered = datagram.ToArray();
        headerTampered[24] ^= 1;
        var payloadTampered = datagram.ToArray();
        payloadTampered[^17] ^= 1;

        Assert.False(VirtualNetworkFrame.TryParse(headerTampered, key, encryptionEnabled, out _));
        Assert.False(VirtualNetworkFrame.TryParse(payloadTampered, key, encryptionEnabled, out _));
        Assert.True(VirtualNetworkFrame.TryParse(valid, key, encryptionEnabled, out var frame));
        Assert.True(VirtualNetworkMeshPacket.TryParse(frame.Payload, out var mesh));
        Assert.Equal(source, frame.VirtualAddress);
        Assert.Equal(destination, mesh.Destination);
        Assert.Equal(2, mesh.HopCount);
        Assert.Equal(8, mesh.HopLimit);
        Assert.Equal(expected, mesh.Payload.ToArray());
    }

    [Theory]
    [InlineData(true, 0)]
    [InlineData(true, 1)]
    [InlineData(true, 1400)]
    [InlineData(false, 0)]
    [InlineData(false, 1)]
    [InlineData(false, 1400)]
    public void FrameParsesAtNonzeroOffsetWithoutCopying(bool encryptionEnabled, int payloadLength)
    {
        var key = RandomNumberGenerator.GetBytes(32);
        var payload = RandomNumberGenerator.GetBytes(payloadLength);
        var datagram = VirtualNetworkFrame.Create(
            VirtualNetworkFrameType.Packet, System.Net.IPAddress.Parse("10.77.0.48"), payload, key, encryptionEnabled);
        var buffer = Enumerable.Repeat((byte)0xcc, datagram.Length + 19).ToArray();
        datagram.CopyTo(buffer, 7);

        Assert.True(VirtualNetworkFrame.TryParse(
            buffer.AsMemory(7, datagram.Length), key, encryptionEnabled, out var frame));

        Assert.Equal(payload, frame.Payload.ToArray());
        Assert.True(MemoryMarshal.TryGetArray(frame.Payload, out var segment));
        Assert.Same(buffer, segment.Array);
        Assert.Equal(7 + (encryptionEnabled ? 52 : 36), segment.Offset);
        Assert.All(buffer.Take(7).Concat(buffer.Skip(7 + datagram.Length)), value => Assert.Equal(0xcc, value));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void TamperingOneAggregatedFrameDoesNotAffectAdjacentAuthentication(bool encryptionEnabled)
    {
        var key = RandomNumberGenerator.GetBytes(32);
        var address = System.Net.IPAddress.Parse("10.77.0.49");
        var payload = RandomNumberGenerator.GetBytes(1400);
        var frames = Enumerable.Range(0, 3).Select(_ => VirtualNetworkFrame.Create(
            VirtualNetworkFrameType.Packet, address, payload, key, encryptionEnabled)).ToArray();
        var buffer = frames.SelectMany(frame => frame).ToArray();
        var length = frames[0].Length;
        buffer[2 * length - 1] ^= 0x40;

        Assert.False(VirtualNetworkFrame.TryParse(
            buffer.AsMemory(length, length), key, encryptionEnabled, out _));
        Assert.Equal(frames[0], buffer.AsSpan(0, length).ToArray());
        Assert.Equal(frames[2], buffer.AsSpan(2 * length, length).ToArray());
        Assert.True(VirtualNetworkFrame.TryParse(
            buffer.AsMemory(0, length), key, encryptionEnabled, out var first));
        Assert.True(VirtualNetworkFrame.TryParse(
            buffer.AsMemory(2 * length, length), key, encryptionEnabled, out var last));
        Assert.Equal(payload, first.Payload.ToArray());
        Assert.Equal(payload, last.Payload.ToArray());
    }

    private static int HeaderProbeOffset(bool encryptionEnabled)
    {
        return encryptionEnabled ? 32 : 41;
    }
}

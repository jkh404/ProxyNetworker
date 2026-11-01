using System.Security.Cryptography;
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

    private static int HeaderProbeOffset(bool encryptionEnabled)
    {
        return encryptionEnabled ? 32 : 41;
    }
}

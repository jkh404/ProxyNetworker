using System.Buffers.Binary;
using System.Net;
using System.Security.Cryptography;

namespace ProxyNetworker.Core;

internal readonly struct VirtualNetworkPathProbe
{
    private const int PayloadSize = 16;

    public VirtualNetworkPathProbe(IPAddress destinationAddress, long timestampMilliseconds, uint nonce)
    {
        DestinationAddress = destinationAddress;
        TimestampMilliseconds = timestampMilliseconds;
        Nonce = nonce;
    }

    public IPAddress DestinationAddress { get; }

    public long TimestampMilliseconds { get; }

    public uint Nonce { get; }

    public static byte[] Create(IPAddress destinationAddress)
    {
        var addressBytes = destinationAddress.GetAddressBytes();
        if (addressBytes.Length != 4)
        {
            throw new PlatformNotSupportedException("Virtual Network path probes currently support IPv4 only.");
        }

        var payload = new byte[PayloadSize];
        addressBytes.CopyTo(payload.AsSpan(0, 4));
        BinaryPrimitives.WriteInt64BigEndian(payload.AsSpan(4, 8), DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        BinaryPrimitives.WriteUInt32BigEndian(payload.AsSpan(12, 4), BitConverter.ToUInt32(RandomNumberGenerator.GetBytes(4)));
        return payload;
    }

    public static bool TryParse(ReadOnlySpan<byte> payload, out VirtualNetworkPathProbe probe)
    {
        probe = default;
        if (payload.Length != PayloadSize)
        {
            return false;
        }

        var address = new IPAddress(payload.Slice(0, 4));
        var timestamp = BinaryPrimitives.ReadInt64BigEndian(payload.Slice(4, 8));
        var nonce = BinaryPrimitives.ReadUInt32BigEndian(payload.Slice(12, 4));
        probe = new VirtualNetworkPathProbe(address, timestamp, nonce);
        return true;
    }
}

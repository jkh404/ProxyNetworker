using System.Buffers.Binary;
using System.Net;
using System.Security.Cryptography;

namespace ProxyNetworker.Core;

internal enum VirtualNetworkFrameType : byte
{
    Hello = 1,
    Packet = 2,
    Heartbeat = 3,
    PeerCandidates = 4,
    PathProbe = 5,
    PathProbeAck = 6,
    MeshLinkState = 7,
    MeshPacket = 8,
    RelayRequired = 9
}

internal readonly struct VirtualNetworkFrame
{
    private const uint Magic = 0x504e564e;
    private const byte Version = 2;
    private const int TimestampOffset = 16;
    private const int NonceOffset = 24;
    private const int TagOffset = 36;
    private const int HeaderSize = 52;
    private const int NonceSize = 12;
    private const int TagSize = 16;
    private const long MaxClockSkewMilliseconds = 300_000;

    public VirtualNetworkFrame(VirtualNetworkFrameType type, IPAddress virtualAddress, ReadOnlyMemory<byte> payload)
    {
        Type = type;
        VirtualAddress = virtualAddress;
        Payload = payload;
    }

    public VirtualNetworkFrameType Type { get; }

    public IPAddress VirtualAddress { get; }

    public ReadOnlyMemory<byte> Payload { get; }

    public static byte[] Create(
        VirtualNetworkFrameType type,
        IPAddress virtualAddress,
        ReadOnlySpan<byte> payload,
        ReadOnlySpan<byte> authenticationKey)
    {
        if (authenticationKey.Length != 32)
        {
            throw new ArgumentException("Virtual Network authentication key must be 32 bytes.", nameof(authenticationKey));
        }

        var addressBytes = virtualAddress.GetAddressBytes();
        if (addressBytes.Length != 4)
        {
            throw new PlatformNotSupportedException("Virtual Network frames currently support IPv4 only.");
        }

        var frame = new byte[HeaderSize + payload.Length];
        BinaryPrimitives.WriteUInt32BigEndian(frame.AsSpan(0, 4), Magic);
        frame[4] = Version;
        frame[5] = (byte)type;
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(6, 2), 0);
        addressBytes.CopyTo(frame.AsSpan(8, 4));
        BinaryPrimitives.WriteUInt32BigEndian(frame.AsSpan(12, 4), checked((uint)payload.Length));
        BinaryPrimitives.WriteInt64BigEndian(frame.AsSpan(TimestampOffset, 8), DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        RandomNumberGenerator.Fill(frame.AsSpan(NonceOffset, NonceSize));

        var plaintext = payload.ToArray();
        var ciphertext = new byte[plaintext.Length];
        using var aes = new AesGcm(authenticationKey, TagSize);
        aes.Encrypt(
            frame.AsSpan(NonceOffset, NonceSize),
            plaintext,
            ciphertext,
            frame.AsSpan(TagOffset, TagSize),
            frame.AsSpan(0, TagOffset));
        ciphertext.CopyTo(frame.AsSpan(HeaderSize));
        return frame;
    }

    public static bool TryParse(byte[] datagram, ReadOnlySpan<byte> authenticationKey, out VirtualNetworkFrame frame)
    {
        frame = default;
        if (authenticationKey.Length != 32 || datagram.Length < HeaderSize)
        {
            return false;
        }

        if (BinaryPrimitives.ReadUInt32BigEndian(datagram.AsSpan(0, 4)) != Magic || datagram[4] != Version)
        {
            return false;
        }

        var type = (VirtualNetworkFrameType)datagram[5];
        if (type is not (VirtualNetworkFrameType.Hello or
            VirtualNetworkFrameType.Packet or
            VirtualNetworkFrameType.Heartbeat or
            VirtualNetworkFrameType.PeerCandidates or
            VirtualNetworkFrameType.PathProbe or
            VirtualNetworkFrameType.PathProbeAck or
            VirtualNetworkFrameType.MeshLinkState or
            VirtualNetworkFrameType.MeshPacket or
            VirtualNetworkFrameType.RelayRequired))
        {
            return false;
        }

        var payloadLength = BinaryPrimitives.ReadUInt32BigEndian(datagram.AsSpan(12, 4));
        if (payloadLength != datagram.Length - HeaderSize)
        {
            return false;
        }

        var timestamp = BinaryPrimitives.ReadInt64BigEndian(datagram.AsSpan(TimestampOffset, 8));
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        if (timestamp < now - MaxClockSkewMilliseconds || timestamp > now + MaxClockSkewMilliseconds)
        {
            return false;
        }

        var addressBytes = new byte[4];
        Buffer.BlockCopy(datagram, 8, addressBytes, 0, addressBytes.Length);
        var ciphertext = datagram.AsSpan(HeaderSize);
        var plaintext = new byte[ciphertext.Length];

        try
        {
            using var aes = new AesGcm(authenticationKey, TagSize);
            aes.Decrypt(
                datagram.AsSpan(NonceOffset, NonceSize),
                ciphertext,
                datagram.AsSpan(TagOffset, TagSize),
                plaintext,
                datagram.AsSpan(0, TagOffset));
        }
        catch (CryptographicException)
        {
            return false;
        }

        frame = new VirtualNetworkFrame(type, new IPAddress(addressBytes), plaintext);
        return true;
    }
}

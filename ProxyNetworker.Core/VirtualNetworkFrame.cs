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
    RelayRequired = 9,
    MeshTrafficReport = 10
}

internal readonly struct VirtualNetworkFrame
{
    private const uint Magic = 0x504e564e;
    private const byte EncryptedVersion = 2;
    private const byte AuthenticatedVersion = 3;
    private const int TimestampOffset = 16;
    private const int NonceOffset = 24;
    private const int NonceSize = VirtualNetworkAead.NonceSize;
    private const int TagOffset = NonceOffset + NonceSize;
    private const int TagSize = VirtualNetworkAead.TagSize;
    private const int HeaderSize = TagOffset + TagSize;
    private const int AuthenticatedHeaderSize = NonceOffset + NonceSize;
    private const long MaxClockSkewMilliseconds = 300_000;

    [ThreadStatic]
    private static HMACSHA256? _hmac;

    [ThreadStatic]
    private static byte[]? _hmacKey;

    [ThreadStatic]
    private static byte[]? _noncePrefix;

    [ThreadStatic]
    private static uint _nonceCounter;

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
        ReadOnlySpan<byte> authenticationKey,
        bool encryptionEnabled = true)
    {
        var frame = new byte[HeaderSize + payload.Length];
        CreateInto(type, virtualAddress, payload, authenticationKey, encryptionEnabled, frame);
        return frame;
    }

    internal static int GetFrameLength(int payloadLength)
    {
        return checked(HeaderSize + payloadLength);
    }

    internal static Span<byte> GetPayloadBuffer(Span<byte> frame, int payloadLength, bool encryptionEnabled)
    {
        if (frame.Length != GetFrameLength(payloadLength))
        {
            throw new ArgumentException("Frame buffer has an unexpected length.", nameof(frame));
        }

        return frame.Slice(encryptionEnabled ? HeaderSize : AuthenticatedHeaderSize, payloadLength);
    }

    internal static void CreateInto(
        VirtualNetworkFrameType type,
        IPAddress virtualAddress,
        ReadOnlySpan<byte> payload,
        ReadOnlySpan<byte> authenticationKey,
        bool encryptionEnabled,
        Span<byte> frame)
    {
        if (authenticationKey.Length != 32)
        {
            throw new ArgumentException("Virtual Network authentication key must be 32 bytes.", nameof(authenticationKey));
        }

        if (!virtualAddress.TryWriteBytes(frame.Slice(8, 4), out var addressBytesWritten) || addressBytesWritten != 4)
        {
            throw new PlatformNotSupportedException("Virtual Network frames currently support IPv4 only.");
        }

        if (frame.Length != GetFrameLength(payload.Length))
        {
            throw new ArgumentException("Frame buffer has an unexpected length.", nameof(frame));
        }

        BinaryPrimitives.WriteUInt32BigEndian(frame[..4], Magic);
        frame[4] = encryptionEnabled ? EncryptedVersion : AuthenticatedVersion;
        frame[5] = (byte)type;
        BinaryPrimitives.WriteUInt16BigEndian(frame.Slice(6, 2), 0);
        BinaryPrimitives.WriteUInt32BigEndian(frame.Slice(12, 4), checked((uint)payload.Length));
        BinaryPrimitives.WriteInt64BigEndian(frame.Slice(TimestampOffset, 8), DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        WriteNonce(frame.Slice(NonceOffset, NonceSize));

        if (encryptionEnabled)
        {
            VirtualNetworkAead.Encrypt(
                authenticationKey,
                frame.Slice(NonceOffset, NonceSize),
                payload,
                frame[HeaderSize..],
                frame.Slice(TagOffset, TagSize),
                frame[..TagOffset]);
            return;
        }

        payload.CopyTo(frame[AuthenticatedHeaderSize..]);
        var authenticatedLength = AuthenticatedHeaderSize + payload.Length;
        Span<byte> fullHash = stackalloc byte[32];
        GetHmac(authenticationKey).TryComputeHash(frame[..authenticatedLength], fullHash, out _);
        fullHash[..TagSize].CopyTo(frame.Slice(authenticatedLength, TagSize));
    }

    internal static void CreateInto(
        VirtualNetworkFrameType type,
        IPAddress virtualAddress,
        ReadOnlySpan<byte> payload,
        ReadOnlySpan<byte> authenticationKey,
        Span<byte> frame)
    {
        CreateInto(type, virtualAddress, payload, authenticationKey, encryptionEnabled: true, frame);
    }

    public static bool TryParse(byte[] datagram, ReadOnlySpan<byte> authenticationKey, bool encryptionEnabled, out VirtualNetworkFrame frame)
    {
        return TryParse(datagram, datagram.Length, authenticationKey, encryptionEnabled, out frame);
    }

    public static bool TryParse(byte[] datagram, ReadOnlySpan<byte> authenticationKey, out VirtualNetworkFrame frame)
    {
        return TryParse(datagram, datagram.Length, authenticationKey, encryptionEnabled: true, out frame);
    }

    public static bool TryParse(byte[] datagram, int datagramLength, ReadOnlySpan<byte> authenticationKey, out VirtualNetworkFrame frame)
    {
        return TryParse(datagram, datagramLength, authenticationKey, encryptionEnabled: true, out frame);
    }

    public static bool TryParse(byte[] datagram, int datagramLength, ReadOnlySpan<byte> authenticationKey, bool encryptionEnabled, out VirtualNetworkFrame frame)
    {
        frame = default;
        if (datagramLength < 0 || datagramLength > datagram.Length)
        {
            return false;
        }

        return TryParse(datagram.AsMemory(0, datagramLength), authenticationKey, encryptionEnabled, out frame);
    }

    internal static bool TryParse(
        Memory<byte> datagram,
        ReadOnlySpan<byte> authenticationKey,
        bool encryptionEnabled,
        out VirtualNetworkFrame frame)
    {
        frame = default;
        if (authenticationKey.Length != 32 || datagram.Length < HeaderSize)
        {
            return false;
        }

        var span = datagram.Span;
        var expectedVersion = encryptionEnabled ? EncryptedVersion : AuthenticatedVersion;
        if (BinaryPrimitives.ReadUInt32BigEndian(span[..4]) != Magic || span[4] != expectedVersion)
        {
            return false;
        }

        var type = (VirtualNetworkFrameType)span[5];
        if (type is not (VirtualNetworkFrameType.Hello or
            VirtualNetworkFrameType.Packet or
            VirtualNetworkFrameType.Heartbeat or
            VirtualNetworkFrameType.PeerCandidates or
            VirtualNetworkFrameType.PathProbe or
            VirtualNetworkFrameType.PathProbeAck or
            VirtualNetworkFrameType.MeshLinkState or
            VirtualNetworkFrameType.MeshPacket or
            VirtualNetworkFrameType.RelayRequired or
            VirtualNetworkFrameType.MeshTrafficReport))
        {
            return false;
        }

        var payloadLength = BinaryPrimitives.ReadUInt32BigEndian(span.Slice(12, 4));
        if (payloadLength != datagram.Length - HeaderSize)
        {
            return false;
        }

        var timestamp = BinaryPrimitives.ReadInt64BigEndian(span.Slice(TimestampOffset, 8));
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        if (timestamp < now - MaxClockSkewMilliseconds || timestamp > now + MaxClockSkewMilliseconds)
        {
            return false;
        }

        ReadOnlyMemory<byte> plaintext;
        if (encryptionEnabled)
        {
            var decrypted = span.Slice(HeaderSize, checked((int)payloadLength));
            if (!VirtualNetworkAead.TryDecryptInPlace(
                authenticationKey,
                span.Slice(NonceOffset, NonceSize),
                decrypted,
                span.Slice(TagOffset, TagSize),
                span[..TagOffset]))
            {
                return false;
            }

            plaintext = datagram.Slice(HeaderSize, decrypted.Length);
        }
        else
        {
            var authenticatedLength = AuthenticatedHeaderSize + checked((int)payloadLength);
            var receivedTag = span.Slice(authenticatedLength, TagSize);
            Span<byte> expectedHash = stackalloc byte[32];
            GetHmac(authenticationKey).TryComputeHash(span[..authenticatedLength], expectedHash, out _);
            if (!CryptographicOperations.FixedTimeEquals(receivedTag, expectedHash[..TagSize]))
            {
                return false;
            }

            plaintext = datagram.Slice(AuthenticatedHeaderSize, checked((int)payloadLength));
        }

        frame = new VirtualNetworkFrame(type, new IPAddress(span.Slice(8, 4)), plaintext);
        return true;
    }

    private static HMACSHA256 GetHmac(ReadOnlySpan<byte> key)
    {
        if (_hmac is not null && _hmacKey is not null && CryptographicOperations.FixedTimeEquals(_hmacKey, key))
        {
            return _hmac;
        }

        _hmac?.Dispose();
        _hmacKey = key.ToArray();
        _hmac = new HMACSHA256(_hmacKey);
        return _hmac;
    }

    private static void WriteNonce(Span<byte> nonce)
    {
        if (_noncePrefix is null)
        {
            _noncePrefix = RandomNumberGenerator.GetBytes(8);
            _nonceCounter = 0;
        }

        var counter = unchecked(++_nonceCounter);
        if (counter == 0)
        {
            RandomNumberGenerator.Fill(_noncePrefix);
            counter = ++_nonceCounter;
        }

        _noncePrefix.CopyTo(nonce[..8]);
        BinaryPrimitives.WriteUInt32BigEndian(nonce[8..12], counter);
    }
}

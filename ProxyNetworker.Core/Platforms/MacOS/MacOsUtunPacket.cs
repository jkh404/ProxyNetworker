using System.Buffers.Binary;

namespace ProxyNetworker.Core.Platforms.MacOS;

internal static class MacOsUtunPacket
{
    internal const int HeaderSize = 4;
    internal const int MaximumPacketSize = 65535;
    internal const uint Ipv4Family = 2;

    internal static bool IsValidIpv4Packet(ReadOnlySpan<byte> packet)
    {
        if (packet.Length is < 20 or > MaximumPacketSize || packet[0] >> 4 != 4)
        {
            return false;
        }

        var headerLength = (packet[0] & 15) * 4;
        return headerLength >= 20 && headerLength <= packet.Length &&
            BinaryPrimitives.ReadUInt16BigEndian(packet.Slice(2, 2)) == packet.Length;
    }

    internal static bool TryGetPacket(ReadOnlyMemory<byte> frame, out ReadOnlyMemory<byte> packet)
    {
        packet = default;
        if (frame.Length < HeaderSize + 20 ||
            BinaryPrimitives.ReadUInt32BigEndian(frame.Span[..HeaderSize]) != Ipv4Family ||
            !IsValidIpv4Packet(frame.Span[HeaderSize..]))
        {
            return false;
        }

        packet = frame[HeaderSize..];
        return true;
    }

    internal static void WriteHeader(Span<byte> header)
    {
        if (header.Length < HeaderSize)
        {
            throw new ArgumentException("The utun address-family header requires four bytes.", nameof(header));
        }

        BinaryPrimitives.WriteUInt32BigEndian(header, Ipv4Family);
    }
}

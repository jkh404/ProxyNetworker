using System.Buffers.Binary;
using System.Net;

namespace ProxyNetworker.Core.Platforms.Linux;

internal static class LinuxUdpOffload
{
    internal const int ProtocolUdp = 17;
    internal const int SegmentOption = 103;
    internal const int GroOption = 104;
    internal const int ControlHeaderSize = 16;
    internal const int ControlSpace = 24;
    internal const int MaxGsoPayloadLength = 65507;
    internal const int MaxGroBufferLength = 128 * 1024;
    internal const int MaxGroSegments = 128;
    internal const int TruncatedFlags = 0x08 | 0x20;

    internal static bool ShouldDisableGso(int error) =>
        error is 5 or 22 or 38 or 90 or 92 or 93 or 95;

    internal static void WriteGsoControl(Span<byte> control, int segmentSize)
    {
        if (control.Length < ControlSpace || segmentSize <= 0 || segmentSize > ushort.MaxValue)
        {
            throw new ArgumentOutOfRangeException(nameof(segmentSize));
        }

        control.Clear();
        BinaryPrimitives.WriteUInt64LittleEndian(control, ControlHeaderSize + sizeof(ushort));
        BinaryPrimitives.WriteInt32LittleEndian(control[8..], ProtocolUdp);
        BinaryPrimitives.WriteInt32LittleEndian(control[12..], SegmentOption);
        BinaryPrimitives.WriteUInt16LittleEndian(control[ControlHeaderSize..], checked((ushort)segmentSize));
    }

    internal static bool TryReadGroSegmentSize(ReadOnlySpan<byte> control, out int segmentSize)
    {
        segmentSize = 0;
        var offset = 0;
        while (offset < control.Length)
        {
            var remaining = control.Length - offset;
            if (remaining < ControlHeaderSize)
            {
                return false;
            }

            var length = BinaryPrimitives.ReadUInt64LittleEndian(control[offset..]);
            if (length < ControlHeaderSize || length > (ulong)remaining)
            {
                return false;
            }

            var level = BinaryPrimitives.ReadInt32LittleEndian(control[(offset + 8)..]);
            var type = BinaryPrimitives.ReadInt32LittleEndian(control[(offset + 12)..]);
            if (level == ProtocolUdp && type == GroOption)
            {
                if (segmentSize != 0 || length != ControlHeaderSize + sizeof(int))
                {
                    return false;
                }

                segmentSize = BinaryPrimitives.ReadInt32LittleEndian(control[(offset + ControlHeaderSize)..]);
                if (segmentSize <= 0 || segmentSize > ushort.MaxValue)
                {
                    return false;
                }
            }

            if (length == (ulong)remaining)
            {
                return true;
            }

            offset += (checked((int)length) + 7) & ~7;
            if (offset > control.Length)
            {
                return false;
            }
        }

        return true;
    }

    internal static int GetSegmentCount(int length, int segmentSize)
    {
        if (length <= 0 || length > MaxGroBufferLength || segmentSize < 0)
        {
            return 0;
        }

        if (segmentSize == 0)
        {
            return length <= MaxGsoPayloadLength ? 1 : 0;
        }

        if (segmentSize > ushort.MaxValue)
        {
            return 0;
        }

        var count = 1 + (length - 1) / segmentSize;
        return count <= MaxGroSegments ? count : 0;
    }

    internal static int BuildSendGroups(
        ReadOnlySpan<LinuxUdpOutboundDatagram> datagrams,
        Span<LinuxUdpSendGroup> groups,
        bool segmentationEnabled)
    {
        var groupCount = 0;
        var index = 0;
        while (index < datagrams.Length)
        {
            var first = datagrams[index];
            var packetCount = 1;
            var payloadLength = first.Length;
            if (segmentationEnabled && first.Length > 0 && first.Length <= MaxGsoPayloadLength)
            {
                while (index + packetCount < datagrams.Length)
                {
                    var next = datagrams[index + packetCount];
                    if (next.Length != first.Length ||
                        !EndPointEquals(first.RemoteEndPoint, next.RemoteEndPoint) ||
                        next.Length > MaxGsoPayloadLength - payloadLength ||
                        packetCount >= 32)
                    {
                        break;
                    }

                    payloadLength += next.Length;
                    packetCount++;
                }
            }

            if (groupCount >= groups.Length)
            {
                throw new ArgumentException("The send-group buffer is too small.", nameof(groups));
            }

            groups[groupCount++] = new LinuxUdpSendGroup(index, packetCount, payloadLength);
            index += packetCount;
        }

        return groupCount;
    }

    internal static int GetSubmittedPacketCount(ReadOnlySpan<LinuxUdpSendGroup> groups, int submittedGroups)
    {
        if (submittedGroups < 0 || submittedGroups > groups.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(submittedGroups));
        }

        var count = 0;
        for (var index = 0; index < submittedGroups; index++)
        {
            count += groups[index].PacketCount;
        }
        return count;
    }

    private static bool EndPointEquals(IPEndPoint left, IPEndPoint right) =>
        left.Port == right.Port && left.Address.Equals(right.Address);
}

internal readonly record struct LinuxUdpOutboundDatagram(byte[] Buffer, int Length, IPEndPoint RemoteEndPoint);

internal readonly record struct LinuxUdpSendGroup(int StartIndex, int PacketCount, int PayloadLength);

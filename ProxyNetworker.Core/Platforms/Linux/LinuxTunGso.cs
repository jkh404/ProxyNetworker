using System.Buffers.Binary;

namespace ProxyNetworker.Core.Platforms.Linux;

internal readonly record struct VirtioNetHeader(
    byte Flags,
    byte GsoType,
    ushort HeaderLength,
    ushort GsoSize,
    ushort ChecksumStart,
    ushort ChecksumOffset)
{
    public static VirtioNetHeader None { get; } = new(0, 0, 0, 0, 0, 0);
}

internal readonly record struct GsoSegment(int Offset, int Length);

internal readonly record struct TcpGsoAggregate(int Length, int GsoSize, int HeaderLength, int TcpOffset, int SegmentCount);

internal static class LinuxTunGso
{
    internal const int VirtioHeaderSize = 10;
    internal const byte FlagNeedsChecksum = 1;
    internal const byte GsoTcpV4 = 1;
    internal const byte GsoEcn = 0x80;
    private const byte IpProtocolTcp = 6;

    internal static bool TryParseFrame(
        ReadOnlySpan<byte> frame,
        out VirtioNetHeader header,
        out ReadOnlySpan<byte> payload)
    {
        header = default;
        payload = default;
        if (frame.Length <= VirtioHeaderSize)
        {
            return false;
        }

        header = new VirtioNetHeader(
            frame[0],
            frame[1],
            BinaryPrimitives.ReadUInt16LittleEndian(frame[2..4]),
            BinaryPrimitives.ReadUInt16LittleEndian(frame[4..6]),
            BinaryPrimitives.ReadUInt16LittleEndian(frame[6..8]),
            BinaryPrimitives.ReadUInt16LittleEndian(frame[8..10]));
        payload = frame[VirtioHeaderSize..];
        return !payload.IsEmpty;
    }

    internal static void WriteHeader(Span<byte> destination, VirtioNetHeader header)
    {
        if (destination.Length < VirtioHeaderSize)
        {
            throw new ArgumentException("The virtio-net header buffer is too small.", nameof(destination));
        }

        destination[0] = header.Flags;
        destination[1] = header.GsoType;
        BinaryPrimitives.WriteUInt16LittleEndian(destination[2..4], header.HeaderLength);
        BinaryPrimitives.WriteUInt16LittleEndian(destination[4..6], header.GsoSize);
        BinaryPrimitives.WriteUInt16LittleEndian(destination[6..8], header.ChecksumStart);
        BinaryPrimitives.WriteUInt16LittleEndian(destination[8..10], header.ChecksumOffset);
    }

    internal static bool TrySegmentTcpV4(
        ReadOnlySpan<byte> packet,
        ushort gsoSize,
        Span<byte> output,
        Span<GsoSegment> segments,
        out int segmentCount)
    {
        segmentCount = 0;
        if (gsoSize == 0 || !TryReadTcpV4Layout(packet, out var layout) || layout.PayloadLength <= gsoSize)
        {
            return false;
        }

        var sourceAddress = packet.Slice(12, 4);
        var destinationAddress = packet.Slice(16, 4);
        var sourceSequence = BinaryPrimitives.ReadUInt32BigEndian(packet[(layout.TcpOffset + 4)..]);
        var outputOffset = 0;
        var consumed = 0;
        var index = 0;

        while (consumed < layout.PayloadLength)
        {
            if (index >= segments.Length)
            {
                segmentCount = 0;
                return false;
            }

            var chunkLength = Math.Min((int)gsoSize, layout.PayloadLength - consumed);
            var segmentLength = layout.TcpOffset + layout.TcpHeaderLength + chunkLength;
            if (outputOffset + segmentLength > output.Length)
            {
                segmentCount = 0;
                return false;
            }

            var segment = output.Slice(outputOffset, segmentLength);
            packet[..layout.TcpOffset].CopyTo(segment);
            packet.Slice(layout.TcpOffset, layout.TcpHeaderLength).CopyTo(segment[layout.TcpOffset..]);
            packet.Slice(layout.PayloadOffset + consumed, chunkLength).CopyTo(segment[layout.PayloadOffset..]);
            if (consumed + chunkLength < layout.PayloadLength)
            {
                // TSO carries terminal PSH/FIN only once. Replicating either flag
                // onto earlier segments makes the peer close before the tail data.
                segment[layout.TcpOffset + 13] &= unchecked((byte)~(0x01 | 0x08));
            }

            BinaryPrimitives.WriteUInt16BigEndian(
                segment[2..4],
                checked((ushort)segmentLength));
            var originalId = BinaryPrimitives.ReadUInt16BigEndian(segment[4..6]);
            BinaryPrimitives.WriteUInt16BigEndian(segment[4..6], unchecked((ushort)(originalId + index)));
            BinaryPrimitives.WriteUInt16BigEndian(segment[10..12], 0);
            BinaryPrimitives.WriteUInt32BigEndian(
                segment[(layout.TcpOffset + 4)..],
                unchecked(sourceSequence + (uint)consumed));
            BinaryPrimitives.WriteUInt16BigEndian(segment[(layout.TcpOffset + 16)..], 0);

            BinaryPrimitives.WriteUInt16BigEndian(segment[10..12], ComputeChecksum(segment[..layout.TcpOffset]));
            BinaryPrimitives.WriteUInt16BigEndian(
                segment[(layout.TcpOffset + 16)..],
                ComputeTcpChecksum(sourceAddress, destinationAddress, segment[layout.TcpOffset..]));

            segments[segmentCount++] = new GsoSegment(outputOffset, segmentLength);
            outputOffset += segmentLength;
            consumed += chunkLength;
            index++;
        }

        return segmentCount > 1;
    }

    internal static bool TryCompleteChecksum(Span<byte> packet, VirtioNetHeader header)
    {
        if ((header.Flags & FlagNeedsChecksum) == 0 || packet.Length < 20 || (packet[0] >> 4) != 4)
        {
            return false;
        }

        var ipHeaderLength = (packet[0] & 0x0f) * 4;
        var ipTotalLength = BinaryPrimitives.ReadUInt16BigEndian(packet[2..4]);
        if (ipHeaderLength < 20 || ipTotalLength < ipHeaderLength || ipTotalLength > packet.Length)
        {
            return false;
        }

        var transport = packet[ipHeaderLength..ipTotalLength];
        var sourceAddress = packet[12..16];
        var destinationAddress = packet[16..20];
        if (packet[9] == IpProtocolTcp && transport.Length >= 20)
        {
            BinaryPrimitives.WriteUInt16BigEndian(transport[16..18], 0);
            BinaryPrimitives.WriteUInt16BigEndian(
                transport[16..18],
                ComputeTcpChecksum(sourceAddress, destinationAddress, transport));
            return true;
        }

        if (packet[9] == 17 && transport.Length >= 8)
        {
            BinaryPrimitives.WriteUInt16BigEndian(transport[6..8], 0);
            var checksum = ComputeUdpChecksum(sourceAddress, destinationAddress, transport);
            BinaryPrimitives.WriteUInt16BigEndian(transport[6..8], checksum == 0 ? ushort.MaxValue : checksum);
            return true;
        }

        return false;
    }

    internal static bool TryBuildTcpV4Aggregate(
        IReadOnlyList<ReadOnlyMemory<byte>> packets,
        int startIndex,
        Span<byte> output,
        out TcpGsoAggregate aggregate)
    {
        aggregate = default;
        var first = packets[startIndex].Span;
        if (!TryReadTcpV4Layout(first, out var layout) || layout.PayloadLength == 0 || layout.PayloadLength > ushort.MaxValue)
        {
            return false;
        }

        var gsoSize = layout.PayloadLength;
        var totalPayloadLength = gsoSize;
        var segmentCount = 1;
        var aggregateLength = layout.TcpOffset + layout.TcpHeaderLength + totalPayloadLength;
        while (startIndex + segmentCount < packets.Count)
        {
            if (layout.PayloadLength < gsoSize || aggregateLength > ushort.MaxValue)
            {
                break;
            }

            var next = packets[startIndex + segmentCount].Span;
            if (!TryReadTcpV4Layout(next, out var nextLayout) ||
                !CanAppendTcpSegment(first, layout, next, nextLayout, totalPayloadLength) ||
                nextLayout.PayloadLength > gsoSize)
            {
                break;
            }

            var nextAggregateLength = layout.TcpOffset + layout.TcpHeaderLength + totalPayloadLength + nextLayout.PayloadLength;
            if (nextAggregateLength > ushort.MaxValue || nextAggregateLength > output.Length)
            {
                break;
            }

            totalPayloadLength += nextLayout.PayloadLength;
            aggregateLength = nextAggregateLength;
            segmentCount++;
            if (nextLayout.PayloadLength < gsoSize)
            {
                break;
            }
        }

        if (segmentCount < 2 || aggregateLength > output.Length)
        {
            return false;
        }

        var aggregateSpan = output[..aggregateLength];
        first[..layout.TcpOffset].CopyTo(aggregateSpan);
        first.Slice(layout.TcpOffset, layout.TcpHeaderLength).CopyTo(aggregateSpan[layout.TcpOffset..]);
        var writeOffset = layout.PayloadOffset;
        for (var index = 0; index < segmentCount; index++)
        {
            var packet = packets[startIndex + index].Span;
            TryReadTcpV4Layout(packet, out var packetLayout);
            packet.Slice(packetLayout.PayloadOffset, packetLayout.PayloadLength).CopyTo(aggregateSpan[writeOffset..]);
            writeOffset += packetLayout.PayloadLength;
        }

        BinaryPrimitives.WriteUInt16BigEndian(aggregateSpan[2..4], checked((ushort)aggregateLength));
        BinaryPrimitives.WriteUInt16BigEndian(aggregateSpan[10..12], 0);
        BinaryPrimitives.WriteUInt16BigEndian(
            aggregateSpan[(layout.TcpOffset + 16)..],
            ComputeTcpPseudoHeaderChecksum(first[12..16], first[16..20], layout.TcpHeaderLength + gsoSize));
        BinaryPrimitives.WriteUInt16BigEndian(aggregateSpan[10..12], ComputeChecksum(aggregateSpan[..layout.TcpOffset]));

        aggregate = new TcpGsoAggregate(
            aggregateLength,
            gsoSize,
            layout.TcpOffset + layout.TcpHeaderLength,
            layout.TcpOffset,
            segmentCount);
        return true;
    }

    private static bool CanAppendTcpSegment(
        ReadOnlySpan<byte> first,
        TcpV4Layout firstLayout,
        ReadOnlySpan<byte> next,
        TcpV4Layout nextLayout,
        int consumedPayloadLength)
    {
        if (nextLayout.TcpOffset != firstLayout.TcpOffset ||
            nextLayout.TcpHeaderLength != firstLayout.TcpHeaderLength ||
            !first[1..2].SequenceEqual(next[1..2]) ||
            !first[8..10].SequenceEqual(next[8..10]) ||
            !first[12..20].SequenceEqual(next[12..20]) ||
            !first.Slice(firstLayout.TcpOffset, 4).SequenceEqual(next.Slice(nextLayout.TcpOffset, 4)) ||
            !first.Slice(firstLayout.TcpOffset + 8, 8).SequenceEqual(next.Slice(nextLayout.TcpOffset + 8, 8)) ||
            !first.Slice(firstLayout.TcpOffset + 18, 2).SequenceEqual(next.Slice(nextLayout.TcpOffset + 18, 2)) ||
            !first.Slice(firstLayout.TcpOffset + 20, firstLayout.TcpHeaderLength - 20).SequenceEqual(
                next.Slice(nextLayout.TcpOffset + 20, nextLayout.TcpHeaderLength - 20)))
        {
            return false;
        }

        var firstSequence = BinaryPrimitives.ReadUInt32BigEndian(first[(firstLayout.TcpOffset + 4)..]);
        var nextSequence = BinaryPrimitives.ReadUInt32BigEndian(next[(nextLayout.TcpOffset + 4)..]);
        return nextSequence == unchecked(firstSequence + (uint)consumedPayloadLength);
    }

    private static bool TryReadTcpV4Layout(ReadOnlySpan<byte> packet, out TcpV4Layout layout)
    {
        layout = default;
        if (packet.Length < 40 || (packet[0] >> 4) != 4 || packet[9] != IpProtocolTcp)
        {
            return false;
        }

        var ipHeaderLength = (packet[0] & 0x0f) * 4;
        if (ipHeaderLength < 20 || packet.Length < ipHeaderLength + 20)
        {
            return false;
        }

        var ipTotalLength = BinaryPrimitives.ReadUInt16BigEndian(packet[2..4]);
        if (ipTotalLength < ipHeaderLength + 20 || ipTotalLength > packet.Length)
        {
            return false;
        }

        var tcpHeaderLength = (packet[ipHeaderLength + 12] >> 4) * 4;
        if (tcpHeaderLength < 20 || ipTotalLength < ipHeaderLength + tcpHeaderLength)
        {
            return false;
        }

        var payloadLength = ipTotalLength - ipHeaderLength - tcpHeaderLength;
        layout = new TcpV4Layout(
            ipHeaderLength,
            ipHeaderLength + tcpHeaderLength,
            tcpHeaderLength,
            payloadLength);
        return true;
    }

    private static ushort ComputeTcpChecksum(
        ReadOnlySpan<byte> sourceAddress,
        ReadOnlySpan<byte> destinationAddress,
        ReadOnlySpan<byte> tcpSegment)
    {
        var sum = SumWords(sourceAddress, 0);
        sum = SumWords(destinationAddress, sum);
        sum += IpProtocolTcp;
        sum += (uint)tcpSegment.Length;
        sum = SumWords(tcpSegment, sum);
        return Fold(sum);
    }

    private static ushort ComputeTcpPseudoHeaderChecksum(
        ReadOnlySpan<byte> sourceAddress,
        ReadOnlySpan<byte> destinationAddress,
        int tcpLength)
    {
        var sum = SumWords(sourceAddress, 0);
        sum = SumWords(destinationAddress, sum);
        sum += IpProtocolTcp;
        sum += (uint)tcpLength;
        return Fold(sum);
    }

    private static ushort ComputeUdpChecksum(
        ReadOnlySpan<byte> sourceAddress,
        ReadOnlySpan<byte> destinationAddress,
        ReadOnlySpan<byte> udpDatagram)
    {
        var sum = SumWords(sourceAddress, 0);
        sum = SumWords(destinationAddress, sum);
        sum += 17;
        sum += (uint)udpDatagram.Length;
        sum = SumWords(udpDatagram, sum);
        return Fold(sum);
    }

    private static ushort ComputeChecksum(ReadOnlySpan<byte> data)
    {
        return Fold(SumWords(data, 0));
    }

    private static uint SumWords(ReadOnlySpan<byte> data, uint sum)
    {
        var index = 0;
        while (index + 1 < data.Length)
        {
            sum += BinaryPrimitives.ReadUInt16BigEndian(data.Slice(index, 2));
            index += 2;
        }

        if (index < data.Length)
        {
            sum += (uint)(data[index] << 8);
        }

        return sum;
    }

    private static ushort Fold(uint sum)
    {
        while ((sum >> 16) != 0)
        {
            sum = (sum & 0xffff) + (sum >> 16);
        }

        var checksum = (ushort)~sum;
        return checksum == 0 ? ushort.MaxValue : checksum;
    }

    private readonly record struct TcpV4Layout(
        int TcpOffset,
        int PayloadOffset,
        int TcpHeaderLength,
        int PayloadLength);
}

using System.Buffers.Binary;
using ProxyNetworker.Core.Platforms.Linux;

namespace ProxyNetworker.Core.Tests;

public sealed class LinuxTunGsoTests
{
    [Fact]
    public void GsoTcpPacketIsSplitIntoTransportSizedPackets()
    {
        var packets = CreateTcpPackets(2, 1000);
        var gsoPacket = CombineTcpPackets(packets);
        var output = new byte[4096];
        var ranges = new GsoSegment[16];

        Assert.True(LinuxTunGso.TrySegmentTcpV4(gsoPacket, 1000, output, ranges, out var count));

        Assert.Equal(2, count);
        Assert.Equal(1040, ranges[0].Length);
        Assert.Equal(1040, ranges[1].Length);
        Assert.Equal(1000, BinaryPrimitives.ReadUInt16BigEndian(output.AsSpan(2, 2)) - 40);
        Assert.Equal(2000u, BinaryPrimitives.ReadUInt32BigEndian(output.AsSpan(24, 4)));
        Assert.True(IsValidIpv4Checksum(output.AsSpan(ranges[0].Offset, ranges[0].Length)));
        Assert.True(IsValidIpv4Checksum(output.AsSpan(ranges[1].Offset, ranges[1].Length)));
    }

    [Fact]
    public void ContiguousTcpPacketsAreCoalescedIntoOneGsoWrite()
    {
        var packets = CreateTcpPackets(3, 1000)
            .Select(static packet => (ReadOnlyMemory<byte>)packet)
            .ToArray();
        var output = new byte[4096];

        Assert.True(LinuxTunGso.TryBuildTcpV4Aggregate(packets, 0, output, out var aggregate));

        Assert.Equal(3, aggregate.SegmentCount);
        Assert.Equal(3040, aggregate.Length);
        Assert.Equal(1000, aggregate.GsoSize);
        Assert.Equal(40, aggregate.HeaderLength);
        Assert.Equal(20, aggregate.TcpOffset);

        var headerBytes = new byte[LinuxTunGso.VirtioHeaderSize];
        LinuxTunGso.WriteHeader(
            headerBytes,
            new VirtioNetHeader(0, LinuxTunGso.GsoTcpV4, 40, 1000, 20, 16));
        Assert.Equal(LinuxTunGso.GsoTcpV4, headerBytes[1]);
        Assert.Equal(1000, BinaryPrimitives.ReadUInt16LittleEndian(headerBytes.AsSpan(4, 2)));
    }

    [Fact]
    public void PartialTransportChecksumIsCompletedForOrdinaryTunPacket()
    {
        var packet = CreateTcpPackets(1, 300)[0];
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(36, 2), 0x1234);
        var header = new VirtioNetHeader(LinuxTunGso.FlagNeedsChecksum, 0, 0, 0, 20, 16);

        Assert.True(LinuxTunGso.TryCompleteChecksum(packet, header));
        Assert.Equal(0, ComputeTcpValidation(packet));
    }

    [Fact]
    public void TerminalTcpFlagsAreKeptOnlyOnTheLastGsoSegment()
    {
        var packets = CreateTcpPackets(2, 1000);
        var gsoPacket = CombineTcpPackets(packets);
        gsoPacket[33] |= 0x09;
        var output = new byte[4096];
        var ranges = new GsoSegment[16];

        Assert.True(LinuxTunGso.TrySegmentTcpV4(gsoPacket, 1000, output, ranges, out var count));

        Assert.Equal(2, count);
        Assert.Equal(0, output[ranges[0].Offset + 33] & 0x09);
        Assert.NotEqual(0, output[ranges[1].Offset + 33] & 0x09);
    }

    private static byte[][] CreateTcpPackets(int count, int payloadLength)
    {
        var packets = new byte[count][];
        for (var index = 0; index < count; index++)
        {
            var packet = new byte[40 + payloadLength];
            packet[0] = 0x45;
            packet[8] = 64;
            packet[9] = 6;
            System.Net.IPAddress.Parse("10.77.0.70").GetAddressBytes().CopyTo(packet, 12);
            System.Net.IPAddress.Parse("10.77.0.5").GetAddressBytes().CopyTo(packet, 16);
            BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(2, 2), checked((ushort)packet.Length));
            BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(4, 2), checked((ushort)(100 + index)));
            BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(10, 2), ComputeChecksum(packet.AsSpan(0, 20)));

            BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(20, 2), 41000);
            BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(22, 2), 443);
            BinaryPrimitives.WriteUInt32BigEndian(packet.AsSpan(24, 4), checked((uint)(2000 + (index * payloadLength))));
            BinaryPrimitives.WriteUInt32BigEndian(packet.AsSpan(28, 4), 9000);
            packet[32] = 0x50;
            packet[33] = 0x10;
            BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(34, 2), 65535);
            packet.AsSpan(40).Fill(checked((byte)(index + 1)));
            BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(36, 2), ComputeTcpChecksum(packet));
            packets[index] = packet;
        }

        return packets;
    }

    private static byte[] CombineTcpPackets(byte[][] packets)
    {
        var payloadLength = packets.Sum(static packet => packet.Length - 40);
        var combined = new byte[40 + payloadLength];
        packets[0].AsSpan(0, 40).CopyTo(combined);
        BinaryPrimitives.WriteUInt16BigEndian(combined.AsSpan(2, 2), checked((ushort)combined.Length));
        var offset = 40;
        foreach (var packet in packets)
        {
            packet.AsSpan(40).CopyTo(combined.AsSpan(offset));
            offset += packet.Length - 40;
        }

        return combined;
    }

    private static bool IsValidIpv4Checksum(ReadOnlySpan<byte> packet)
    {
        var headerLength = (packet[0] & 0x0f) * 4;
        return ComputeChecksum(packet[..headerLength]) == 0;
    }

    private static ushort ComputeChecksum(ReadOnlySpan<byte> data)
    {
        uint sum = 0;
        for (var index = 0; index + 1 < data.Length; index += 2)
        {
            sum += BinaryPrimitives.ReadUInt16BigEndian(data.Slice(index, 2));
        }

        if ((data.Length & 1) != 0)
        {
            sum += (uint)(data[^1] << 8);
        }

        while ((sum >> 16) != 0)
        {
            sum = (sum & 0xffff) + (sum >> 16);
        }

        return (ushort)~sum;
    }

    private static ushort ComputeTcpChecksum(ReadOnlySpan<byte> packet)
    {
        var span = new byte[12 + packet.Length - 20];
        packet[12..20].CopyTo(span);
        span[9] = 6;
        BinaryPrimitives.WriteUInt16BigEndian(span.AsSpan(10, 2), checked((ushort)(packet.Length - 20)));
        packet[20..].CopyTo(span.AsSpan(12));
        span[12 + 16] = 0;
        span[12 + 17] = 0;
        return ComputeChecksum(span);
    }

    private static ushort ComputeTcpValidation(ReadOnlySpan<byte> packet)
    {
        var span = new byte[12 + packet.Length - 20];
        packet[12..20].CopyTo(span);
        span[9] = 6;
        BinaryPrimitives.WriteUInt16BigEndian(span.AsSpan(10, 2), checked((ushort)(packet.Length - 20)));
        packet[20..].CopyTo(span.AsSpan(12));
        return ComputeChecksum(span);
    }
}

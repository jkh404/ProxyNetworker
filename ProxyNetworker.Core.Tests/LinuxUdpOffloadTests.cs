using System.Buffers.Binary;
using System.Net;
using ProxyNetworker.Core.Platforms.Linux;

namespace ProxyNetworker.Core.Tests;

public sealed class LinuxUdpOffloadTests
{
    [Fact]
    public void GsoControlUsesUshortPayloadAndNative64BitAlignment()
    {
        var control = new byte[LinuxUdpOffload.ControlSpace];
        control.AsSpan().Fill(0xcc);

        LinuxUdpOffload.WriteGsoControl(control, 1400);

        Assert.Equal(18ul, BinaryPrimitives.ReadUInt64LittleEndian(control));
        Assert.Equal(17, BinaryPrimitives.ReadInt32LittleEndian(control.AsSpan(8)));
        Assert.Equal(103, BinaryPrimitives.ReadInt32LittleEndian(control.AsSpan(12)));
        Assert.Equal(1400, BinaryPrimitives.ReadUInt16LittleEndian(control.AsSpan(16)));
        Assert.All(control.Skip(18), value => Assert.Equal(0, value));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(65536)]
    public void GsoControlRejectsInvalidSegmentSize(int length) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => LinuxUdpOffload.WriteGsoControl(new byte[24], length));

    [Fact]
    public void GroControlUsesIntPayloadAndAcceptsMissingMetadata()
    {
        Assert.True(LinuxUdpOffload.TryReadGroSegmentSize([], out var size));
        Assert.Equal(0, size);
        var control = CreateGroControl(1400);
        Assert.True(LinuxUdpOffload.TryReadGroSegmentSize(control, out size));
        Assert.Equal(1400, size);
        Assert.True(LinuxUdpOffload.TryReadGroSegmentSize(control.AsSpan(0, 20), out size));
        Assert.Equal(1400, size);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(65536)]
    public void GroControlRejectsInvalidSegmentSize(int size) =>
        Assert.False(LinuxUdpOffload.TryReadGroSegmentSize(CreateGroControl(size), out _));

    [Theory]
    [InlineData(0)]
    [InlineData(15)]
    [InlineData(18)]
    [InlineData(19)]
    [InlineData(21)]
    [InlineData(25)]
    [InlineData(ulong.MaxValue)]
    public void GroControlRejectsMalformedLengths(ulong length)
    {
        var control = CreateGroControl(1400);
        BinaryPrimitives.WriteUInt64LittleEndian(control, length);
        Assert.False(LinuxUdpOffload.TryReadGroSegmentSize(control, out _));
    }

    [Fact]
    public void GroControlRejectsTruncationAndDuplicateMetadata()
    {
        var control = CreateGroControl(1400);
        for (var length = 1; length < 20; length++)
        {
            Assert.False(LinuxUdpOffload.TryReadGroSegmentSize(control.AsSpan(0, length), out _));
        }

        var duplicate = control.Concat(control).ToArray();
        Assert.False(LinuxUdpOffload.TryReadGroSegmentSize(duplicate, out _));
        Assert.False(LinuxUdpOffload.TryReadGroSegmentSize(control.AsSpan(0, 21), out _));
    }

    [Fact]
    public void GroControlSkipsOtherAncillaryMessages()
    {
        var control = CreateGroControl(100).Concat(CreateGroControl(1400)).ToArray();
        BinaryPrimitives.WriteInt32LittleEndian(control.AsSpan(8), 0);
        Assert.True(LinuxUdpOffload.TryReadGroSegmentSize(control, out var size));
        Assert.Equal(1400, size);
    }

    [Theory]
    [InlineData(1400, 0, 1)]
    [InlineData(65508, 0, 0)]
    [InlineData(0, 1400, 0)]
    [InlineData(1400, -1, 0)]
    [InlineData(2800, 1400, 2)]
    [InlineData(2801, 1400, 3)]
    [InlineData(100, 1400, 1)]
    [InlineData(1025, 1024, 2)]
    [InlineData(131072, 1024, 128)]
    [InlineData(131073, 1024, 0)]
    [InlineData(129, 1, 0)]
    [InlineData(int.MaxValue, 1, 0)]
    [InlineData(100, 65536, 0)]
    public void GroSplittingIsBoundedAndAllowsShortTail(int length, int size, int count) =>
        Assert.Equal(count, LinuxUdpOffload.GetSegmentCount(length, size));

    [Fact]
    public void GroupingPreservesOrderAndEndpointAndLengthBoundaries()
    {
        var endpointA = new IPEndPoint(IPAddress.Loopback, 10001);
        var endpointB = new IPEndPoint(IPAddress.Loopback, 10002);
        var endpointC = new IPEndPoint(IPAddress.Parse("127.0.0.2"), 10002);
        LinuxUdpOutboundDatagram[] datagrams =
        [
            Packet(1400, endpointA), Packet(1400, new IPEndPoint(IPAddress.Loopback, 10001)),
            Packet(100, endpointA), Packet(100, endpointB), Packet(100, endpointC),
            Packet(1400, endpointA), Packet(1400, endpointA)
        ];
        var groups = new LinuxUdpSendGroup[datagrams.Length];

        var count = LinuxUdpOffload.BuildSendGroups(datagrams, groups, true);

        Assert.Equal(5, count);
        Assert.Equal(new LinuxUdpSendGroup(0, 2, 2800), groups[0]);
        Assert.Equal(new LinuxUdpSendGroup(2, 1, 100), groups[1]);
        Assert.Equal(new LinuxUdpSendGroup(3, 1, 100), groups[2]);
        Assert.Equal(new LinuxUdpSendGroup(4, 1, 100), groups[3]);
        Assert.Equal(new LinuxUdpSendGroup(5, 2, 2800), groups[4]);
    }

    [Fact]
    public void GroupingCapsAggregateSizeAndSupportsDisabledOffload()
    {
        var endpoint = new IPEndPoint(IPAddress.Loopback, 10001);
        var datagrams = Enumerable.Range(0, 5).Select(_ => Packet(30000, endpoint)).ToArray();
        var groups = new LinuxUdpSendGroup[5];
        Assert.Equal(3, LinuxUdpOffload.BuildSendGroups(datagrams, groups, true));
        Assert.Equal(new LinuxUdpSendGroup(0, 2, 60000), groups[0]);
        Assert.Equal(new LinuxUdpSendGroup(2, 2, 60000), groups[1]);
        Assert.Equal(new LinuxUdpSendGroup(4, 1, 30000), groups[2]);
        Assert.Equal(5, LinuxUdpOffload.BuildSendGroups(datagrams, groups, false));
        Assert.All(groups, group => Assert.Equal(1, group.PacketCount));
    }

    [Fact]
    public void PartialSubmissionMapsGroupsBackToOriginalDatagrams()
    {
        LinuxUdpSendGroup[] groups = [new(0, 1, 100), new(1, 20, 28000), new(21, 11, 15400)];
        Assert.Equal(0, LinuxUdpOffload.GetSubmittedPacketCount(groups, 0));
        Assert.Equal(1, LinuxUdpOffload.GetSubmittedPacketCount(groups, 1));
        Assert.Equal(21, LinuxUdpOffload.GetSubmittedPacketCount(groups, 2));
        Assert.Equal(32, LinuxUdpOffload.GetSubmittedPacketCount(groups, 3));
        Assert.Throws<ArgumentOutOfRangeException>(() => LinuxUdpOffload.GetSubmittedPacketCount(groups, 4));
    }

    [Theory]
    [InlineData(22, true)]
    [InlineData(95, true)]
    [InlineData(90, true)]
    [InlineData(5, true)]
    [InlineData(11, false)]
    [InlineData(4, false)]
    [InlineData(105, false)]
    public void OnlyCapabilityAndPathErrorsDisableGso(int error, bool disabled) =>
        Assert.Equal(disabled, LinuxUdpOffload.ShouldDisableGso(error));

    private static LinuxUdpOutboundDatagram Packet(int length, IPEndPoint endpoint) => new(new byte[length], length, endpoint);

    private static byte[] CreateGroControl(int size)
    {
        var control = new byte[24];
        BinaryPrimitives.WriteUInt64LittleEndian(control, 20);
        BinaryPrimitives.WriteInt32LittleEndian(control.AsSpan(8), 17);
        BinaryPrimitives.WriteInt32LittleEndian(control.AsSpan(12), 104);
        BinaryPrimitives.WriteInt32LittleEndian(control.AsSpan(16), size);
        return control;
    }
}

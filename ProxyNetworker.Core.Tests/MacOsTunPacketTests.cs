using System.Buffers.Binary;
using System.Net;
using System.Runtime.InteropServices;
using ProxyNetworker.Core.Platforms.MacOS;

namespace ProxyNetworker.Core.Tests;

public sealed class MacOsTunPacketTests
{
    [Fact]
    public void HeaderUsesDarwinNetworkByteOrder()
    {
        var header = new byte[4];
        MacOsUtunPacket.WriteHeader(header);
        Assert.Equal(new byte[] { 0, 0, 0, 2 }, header);
    }

    [Fact]
    public void ParsedPacketIsAViewWithoutTheFamilyHeader()
    {
        var packet = CreatePacket(12);
        var frame = Frame(packet);
        Assert.True(MacOsUtunPacket.TryGetPacket(frame, out var parsed));
        Assert.True(parsed.Span.SequenceEqual(packet));
        frame[24] = 123;
        Assert.Equal(123, parsed.Span[20]);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(19)]
    public void ShortPacketsAreRejected(int size)
    {
        Assert.False(MacOsUtunPacket.IsValidIpv4Packet(new byte[size]));
        Assert.False(MacOsUtunPacket.TryGetPacket(Frame(new byte[size]), out _));
    }

    [Theory]
    [InlineData(0u)]
    [InlineData(30u)]
    [InlineData(0x02000000u)]
    public void OtherFamiliesAndHostEndianHeadersAreRejected(uint family)
    {
        var frame = Frame(CreatePacket(0));
        BinaryPrimitives.WriteUInt32BigEndian(frame, family);
        Assert.False(MacOsUtunPacket.TryGetPacket(frame, out _));
    }

    [Theory]
    [InlineData(0x44)]
    [InlineData(0x46)]
    [InlineData(0x60)]
    public void InvalidIpv4HeaderLengthsAndIpv6AreRejected(byte version)
    {
        var packet = CreatePacket(0);
        packet[0] = version;
        Assert.False(MacOsUtunPacket.IsValidIpv4Packet(packet));
    }

    [Fact]
    public void TruncatedAndOversizedPacketsAreRejected()
    {
        var packet = CreatePacket(4);
        Assert.False(MacOsUtunPacket.IsValidIpv4Packet(packet.AsSpan(0, packet.Length - 1)));
        Assert.False(MacOsUtunPacket.IsValidIpv4Packet(new byte[65536]));
        Assert.False(MacOsUtunPacket.TryGetPacket(new byte[3], out _));
        Assert.Throws<ArgumentException>(() => MacOsUtunPacket.WriteHeader(new byte[3]));
    }

    [Fact]
    public void MaximumIpv4PacketAndOptionsAreAccepted()
    {
        Assert.True(MacOsUtunPacket.IsValidIpv4Packet(CreatePacket(65535 - 20)));
        var options = CreatePacket(4);
        options[0] = 0x46;
        Assert.True(MacOsUtunPacket.IsValidIpv4Packet(options));
    }

    [Fact]
    public void DarwinStructuresHaveTheirNativeSizesAndOffsets()
    {
        Assert.Equal(100, Marshal.SizeOf<MacOsTunNative.ControlInfo>());
        Assert.Equal(32, Marshal.SizeOf<MacOsTunNative.ControlAddress>());
        Assert.Equal(8, Marshal.SizeOf<MacOsTunNative.PollDescriptor>());
        Assert.Equal(IntPtr.Size * 2, Marshal.SizeOf<MacOsTunNative.IoVector>());
        Assert.Equal(4, Marshal.OffsetOf<MacOsTunNative.ControlAddress>("Id").ToInt32());
        Assert.Equal(8, Marshal.OffsetOf<MacOsTunNative.ControlAddress>("Unit").ToInt32());
    }

    [Fact]
    public void ConfigurationUsesArgumentListsAndOnlyTheVirtualSubnet()
    {
        var options = new TunDeviceOptions
        {
            Name = "logical name; not a shell command",
            Address = IPAddress.Parse("198.18.7.9"),
            PrefixLength = 24,
            Mtu = 1400
        };
        var commands = MacOsTunConfiguration.CreateCommands("utun42", options);
        Assert.Equal(2, commands.Length);
        Assert.Equal("/sbin/ifconfig", commands[0].FileName);
        Assert.Equal(
            ["utun42", "inet", "198.18.7.9", "198.18.7.9", "netmask", "255.255.255.0", "mtu", "1400", "up"],
            commands[0].Arguments);
        Assert.Equal("/sbin/route", commands[1].FileName);
        Assert.Equal(["-n", "add", "-inet", "-net", "198.18.7.0/24", "-interface", "utun42"], commands[1].Arguments);
        Assert.DoesNotContain(commands, command => command.Arguments.Contains("delete") || command.Arguments.Contains("change"));
    }

    [Theory]
    [InlineData(1, "128.0.0.0/1", "128.0.0.0")]
    [InlineData(31, "198.18.7.8/31", "255.255.255.254")]
    [InlineData(32, "198.18.7.9/32", "255.255.255.255")]
    public void PrefixesUseTheCorrectNetworkAndMask(int prefix, string network, string mask)
    {
        var commands = MacOsTunConfiguration.CreateCommands("utun0", new TunDeviceOptions
        {
            Address = IPAddress.Parse("198.18.7.9"),
            PrefixLength = prefix
        });
        Assert.Contains(mask, commands[0].Arguments);
        if (prefix == 32)
        {
            Assert.Single(commands);
        }
        else
        {
            Assert.Contains(network, commands[1].Arguments);
        }
    }

    [Theory]
    [InlineData(0, "add net 198.18.7.0/24: gateway utun42", "", true)]
    [InlineData(0, "add net 198.18.7.0/24: gateway utun42: File exists", "", false)]
    [InlineData(0, "", "route: writing to routing socket: File exists", false)]
    [InlineData(0, "", "", false)]
    [InlineData(1, "add net 198.18.7.0/24: gateway utun42", "", false)]
    public void RouteErrorsCannotBeHiddenByZeroExitStatus(int code, string output, string error, bool succeeds)
    {
        var route = MacOsTunConfiguration.CreateCommands("utun42", new()
        {
            Address = IPAddress.Parse("198.18.7.9"),
            PrefixLength = 24
        })[1];
        if (succeeds)
        {
            MacOsTunConfiguration.EnsureSucceeded(route, code, output, error);
        }
        else
        {
            Assert.Throws<IOException>(() => MacOsTunConfiguration.EnsureSucceeded(route, code, output, error));
        }
    }

    [Theory]
    [InlineData("198.18.7.9", 24, "198.18.7.3", 32, true)]
    [InlineData("198.18.7.9", 32, "198.18.7.3", 24, true)]
    [InlineData("198.18.7.9", 24, "198.18.8.3", 24, false)]
    [InlineData("198.18.7.9", 32, "198.18.7.9", 32, true)]
    [InlineData("198.18.7.9", 32, "198.18.7.8", 32, false)]
    public void ConnectedSubnetConflictsAreDetectedBeforeConfiguration(
        string first, int firstPrefix, string second, int secondPrefix, bool expected)
    {
        Assert.Equal(expected, MacOsTunConfiguration.NetworksOverlap(
            IPAddress.Parse(first), firstPrefix, IPAddress.Parse(second), secondPrefix));
    }

    [Theory]
    [InlineData("utun")]
    [InlineData("en0")]
    [InlineData("utun1;id")]
    [InlineData("utun-1")]
    [InlineData("utun123456789012")]
    public void UnsafeOrInvalidKernelNamesAreRejected(string name)
    {
        Assert.Throws<ArgumentException>(() => MacOsTunConfiguration.CreateCommands(name, new()));
    }

    [Fact]
    public void DefaultRouteAndIpv6ConfigurationAreRejected()
    {
        Assert.Throws<ArgumentException>(() => MacOsTunConfiguration.Validate(new() { PrefixLength = 0 }));
        Assert.Throws<PlatformNotSupportedException>(() =>
            MacOsTunConfiguration.Validate(new() { Address = IPAddress.IPv6Loopback }));
    }

    [Fact]
    public void NativeSocketCreationIsGuardedOnOtherOperatingSystems()
    {
        if (!OperatingSystem.IsMacOS())
        {
            Assert.Throws<PlatformNotSupportedException>(() => MacOsTunConnection.Create());
        }
    }

    internal static byte[] CreatePacket(int payload)
    {
        var packet = new byte[20 + payload];
        packet[0] = 0x45;
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(2), (ushort)packet.Length);
        return packet;
    }

    internal static byte[] Frame(byte[] packet)
    {
        var frame = new byte[packet.Length + 4];
        MacOsUtunPacket.WriteHeader(frame);
        packet.CopyTo(frame, 4);
        return frame;
    }
}

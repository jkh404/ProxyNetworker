using System.Buffers;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using ProxyNetworker.Core.Platforms.Linux;

namespace ProxyNetworker.Core.Tests;

public sealed class LinuxUdpBatchSocketTests
{
    [LinuxTheory]
    [InlineData(false, false, false)]
    [InlineData(false, false, true)]
    [InlineData(false, true, false)]
    [InlineData(false, true, true)]
    [InlineData(true, false, false)]
    [InlineData(true, false, true)]
    [InlineData(true, true, false)]
    [InlineData(true, true, true)]
    public void NativeOffloadPreservesIndependentAuthenticatedFrames(bool gso, bool gro, bool encrypted)
    {
        using var source = CreateSocket();
        using var destination = CreateSocket();
        using var sender = CreateTransport(source, 2048, gso, false);
        using var receiver = CreateTransport(destination, 2048, false, gro);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var key = RandomNumberGenerator.GetBytes(32);
        var address = IPAddress.Parse("10.77.0.52");
        var endpoint = (IPEndPoint)destination.LocalEndPoint!;
        var batch = Enumerable.Range(0, 32).Select(index =>
        {
            var payload = Enumerable.Repeat((byte)index, 1400).ToArray();
            var frame = VirtualNetworkFrame.Create(VirtualNetworkFrameType.Packet, address, payload, key, encrypted);
            return new LinuxUdpOutboundDatagram(frame, frame.Length, endpoint);
        }).ToArray();

        SendAll(sender, batch, timeout.Token);

        var received = 0;
        var slicedPackets = 0;
        while (received < batch.Length)
        {
            var count = receiver.ReceiveBatch(timeout.Token);
            for (var index = 0; index < count; index++)
            {
                var datagram = receiver.GetReceived(index);
                Assert.Equal(source.LocalEndPoint, datagram.RemoteEndPoint);
                Assert.True(VirtualNetworkFrame.TryParse(datagram.Memory, key, encrypted, out var frame));
                Assert.Equal(address, frame.VirtualAddress);
                Assert.Equal(1400, frame.Payload.Length);
                Assert.All(frame.Payload.ToArray(), value => Assert.Equal((byte)received, value));
                Assert.True(MemoryMarshal.TryGetArray(frame.Payload, out var view));
                Assert.Same(datagram.Buffer, view.Array);
                if (datagram.Offset > 0)
                {
                    slicedPackets++;
                }
                received++;
            }
        }

        Assert.Equal(32, received);
        Assert.Equal(gso ? 32 : 0, sender.GsoDatagramsSent);
        if (gso && gro)
        {
            Assert.InRange(receiver.GroDatagramsReceived, 2, 32);
            Assert.True(slicedPackets > 0);
        }
        else if (!gro)
        {
            Assert.Equal(0, receiver.GroDatagramsReceived);
        }
    }

    [LinuxFact]
    public void NativeGsoFailureAfterPartialSubmissionRetriesOnlyUnsentPackets()
    {
        using var source = CreateSocket();
        using var destination = CreateSocket();
        var logs = new ConcurrentQueue<string>();
        using var sender = LinuxUdpBatchSocket.TryCreate(
            source, 2048, entry => logs.Enqueue(entry.Message), enableUdpGso: true, enableUdpGro: false);
        Assert.NotNull(sender);
        Assert.True(sender.UdpGsoEnabled);
        using var receiver = CreateTransport(destination, 2048, false, false);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        // SO_NO_CHECK deliberately rejects GSO, but still permits ordinary UDP on loopback.
        var noChecksum = 1;
        Assert.Equal(0, SetSocketOption(source.SafeHandle, 1, 11, ref noChecksum, sizeof(int)));
        var endpoint = (IPEndPoint)destination.LocalEndPoint!;
        var batch = Enumerable.Range(0, 5).Select(index =>
        {
            var data = Enumerable.Repeat((byte)index, index == 0 ? 17 : 128).ToArray();
            return new LinuxUdpOutboundDatagram(data, data.Length, endpoint);
        }).ToArray();

        Assert.Equal(1, sender.SendBatch(batch, timeout.Token));
        Assert.True(sender.UdpGsoEnabled);
        Assert.Equal(0, sender.SendBatch(batch.AsSpan(1), timeout.Token));
        Assert.False(sender.UdpGsoEnabled);
        Assert.Equal(4, sender.SendBatch(batch.AsSpan(1), timeout.Token));

        var received = ReceiveCopies(receiver, 5, timeout.Token);
        Assert.Equal(5, received.Count);
        for (var index = 0; index < received.Count; index++)
        {
            Assert.Equal(batch[index].Buffer, received[index]);
        }
        Assert.Equal(0, receiver.ReceiveBatch(timeout.Token));
        Assert.Single(logs, message => message.Contains("retrying unsent", StringComparison.Ordinal));
    }

    [LinuxTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void NativeReceiveRejectsOversizedDatagrams(bool gro)
    {
        using var source = CreateSocket();
        using var destination = CreateSocket();
        using var receiver = CreateTransport(destination, 512, false, gro);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        source.SendTo(new byte[1024], destination.LocalEndPoint!);
        source.SendTo(new byte[] { 42 }, destination.LocalEndPoint!);

        var received = ReceiveCopies(receiver, 1, timeout.Token);
        Assert.Single(received);
        Assert.Equal(new byte[] { 42 }, received[0]);
        Assert.Equal(0, receiver.ReceiveBatch(timeout.Token));
    }

    [LinuxFact]
    public void NativeReceiveKeepsShortGroTailAfterOversizedSegments()
    {
        using var source = CreateSocket();
        using var destination = CreateSocket();
        using var receiver = CreateTransport(destination, 512, false, true);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var segmentSize = 1024;
        Assert.Equal(0, SetSocketOption(source.SafeHandle, 17, 103, ref segmentSize, sizeof(int)));
        var aggregate = Enumerable.Repeat((byte)0x11, 2055).ToArray();
        aggregate.AsSpan(2048).Fill(0x42);

        source.SendTo(aggregate, destination.LocalEndPoint!);

        var received = ReceiveCopies(receiver, 1, timeout.Token);
        Assert.Single(received);
        Assert.Equal(Enumerable.Repeat((byte)0x42, 7).ToArray(), received[0]);
        Assert.Equal(0, receiver.ReceiveBatch(timeout.Token));
    }

    [LinuxFact]
    public async Task NativeSendQueueTransfersPooledBufferOwnership()
    {
        using var source = CreateSocket();
        using var destination = CreateSocket();
        using var sender = CreateTransport(source, 2048, true, false);
        using var receiver = CreateTransport(destination, 2048, false, true);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var endpoint = (IPEndPoint)destination.LocalEndPoint!;
        var receive = Task.Run(() => ReceiveCopies(receiver, 128, timeout.Token));

        for (var index = 0; index < 128; index++)
        {
            var buffer = ArrayPool<byte>.Shared.Rent(1400);
            buffer.AsSpan(0, 1400).Fill((byte)index);
            Assert.True(sender.EnqueueOwned(buffer, 1400, endpoint));
        }

        var received = await receive.WaitAsync(TimeSpan.FromSeconds(12));
        Assert.Equal(128, received.Count);
        for (var index = 0; index < received.Count; index++)
        {
            Assert.Equal(1400, received[index].Length);
            Assert.All(received[index], value => Assert.Equal((byte)index, value));
        }
    }

    [LinuxTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NativeIdleReceiveShutsDownWithoutFreeingActiveMemory(bool closeSocketFirst)
    {
        using var socket = CreateSocket();
        using var transport = CreateTransport(socket, 2048, true, true);
        using var entered = new ManualResetEventSlim();
        var receive = Task.Run(() =>
        {
            entered.Set();
            try
            {
                while (true)
                {
                    transport.ReceiveBatch(CancellationToken.None);
                }
            }
            catch (ObjectDisposedException)
            {
            }
        });
        Assert.True(entered.Wait(TimeSpan.FromSeconds(2)));
        await Task.Delay(50);

        if (closeSocketFirst)
        {
            socket.Dispose();
        }
        await Task.Run(transport.Dispose).WaitAsync(TimeSpan.FromSeconds(3));
        await receive.WaitAsync(TimeSpan.FromSeconds(3));

        Assert.Throws<ObjectDisposedException>(() => transport.ReceiveBatch(CancellationToken.None));
        var rejected = ArrayPool<byte>.Shared.Rent(100);
        Assert.False(transport.EnqueueOwned(rejected, 100, new IPEndPoint(IPAddress.Loopback, 10000)));
        ArrayPool<byte>.Shared.Return(rejected);
    }

    private static Socket CreateSocket()
    {
        var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        socket.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        return socket;
    }

    private static LinuxUdpBatchSocket CreateTransport(Socket socket, int maximum, bool gso, bool gro)
    {
        var transport = LinuxUdpBatchSocket.TryCreate(socket, maximum, enableUdpGso: gso, enableUdpGro: gro);
        Assert.NotNull(transport);
        Assert.Equal(gso, transport.UdpGsoEnabled);
        Assert.Equal(gro, transport.UdpGroEnabled);
        return transport;
    }

    private static void SendAll(LinuxUdpBatchSocket sender, LinuxUdpOutboundDatagram[] batch, CancellationToken token)
    {
        var sent = 0;
        while (sent < batch.Length)
        {
            token.ThrowIfCancellationRequested();
            sent += sender.SendBatch(batch.AsSpan(sent), token);
        }
    }

    private static List<byte[]> ReceiveCopies(LinuxUdpBatchSocket receiver, int expected, CancellationToken token)
    {
        var result = new List<byte[]>();
        while (result.Count < expected)
        {
            var count = receiver.ReceiveBatch(token);
            for (var index = 0; index < count; index++)
            {
                result.Add(receiver.GetReceived(index).Memory.ToArray());
            }
        }
        return result;
    }

    [DllImport("libc", EntryPoint = "setsockopt", SetLastError = true)]
    private static extern int SetSocketOption(SafeSocketHandle socket, int level, int option, ref int value, uint length);
}

internal sealed class LinuxFactAttribute : FactAttribute
{
    public LinuxFactAttribute()
    {
        if (!OperatingSystem.IsLinux())
        {
            Skip = "Requires native Linux UDP syscalls.";
        }
    }
}

internal sealed class LinuxTheoryAttribute : TheoryAttribute
{
    public LinuxTheoryAttribute()
    {
        if (!OperatingSystem.IsLinux())
        {
            Skip = "Requires native Linux UDP syscalls.";
        }
    }
}

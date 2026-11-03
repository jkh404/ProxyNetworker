using System.Collections.Concurrent;
using System.Net;
using ProxyNetworker.Core.Platforms.MacOS;

namespace ProxyNetworker.Core.Tests;

public sealed class MacOsTunDeviceTests
{
    [Fact]
    public async Task StartUsesKernelNameAndIsIdempotent()
    {
        var platform = new FakePlatform();
        using var device = new MacOsTunDevice(new() { Name = "test-node" }, platform);
        await device.StartAsync();
        await device.StartAsync();
        Assert.Equal("utun42", device.Name);
        Assert.True(device.IsStarted);
        Assert.Single(platform.Created);
        Assert.Equal(1, platform.Configurations);
    }

    [Fact]
    public async Task OptionsAreSnapshottedAndConfigurationCanBeDisabled()
    {
        var options = new TunDeviceOptions { Address = IPAddress.Parse("198.18.0.1") };
        var platform = new FakePlatform();
        using var device = new MacOsTunDevice(options, platform);
        options.Address = IPAddress.Parse("192.0.2.1");
        await device.StartAsync();
        Assert.Equal("198.18.0.1", platform.LastOptions!.Address.ToString());
        var other = new FakePlatform();
        using var unconfigured = new MacOsTunDevice(new() { ConfigureInterface = false }, other);
        await unconfigured.StartAsync();
        Assert.Equal(0, other.Configurations);
    }

    [Fact]
    public async Task ConfigurationFailureClosesOnlyNewConnectionAndAllowsRetry()
    {
        var platform = new FakePlatform
        {
            Configure = _ => throw new IOException("route already exists")
        };
        using var device = new MacOsTunDevice(new(), platform);
        await Assert.ThrowsAsync<IOException>(() => device.StartAsync());
        Assert.False(device.IsStarted);
        Assert.Equal(1, platform.Created[0].DisposeCount);
        platform.Configure = _ => Task.CompletedTask;
        await device.StartAsync();
        Assert.True(device.IsStarted);
        Assert.Equal(2, platform.Created.Count);
        Assert.Equal(0, platform.Created[1].DisposeCount);
    }

    [Fact]
    public async Task CancelledStartDoesNotAllocateAConnection()
    {
        var platform = new FakePlatform();
        using var device = new MacOsTunDevice(new(), platform);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => device.StartAsync(cancellation.Token));
        Assert.Empty(platform.Created);
    }

    [Fact]
    public async Task DisposeCancelsInProgressConfigurationWithoutLeaking()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var platform = new FakePlatform
        {
            Configure = async token =>
            {
                entered.SetResult();
                await Task.Delay(Timeout.Infinite, token);
            }
        };
        var device = new MacOsTunDevice(new(), platform);
        var start = device.StartAsync();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await Task.Run(device.Dispose).WaitAsync(TimeSpan.FromSeconds(5));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => start);
        Assert.Equal(1, platform.Created[0].DisposeCount);
        Assert.False(device.IsStarted);
    }

    [Fact]
    public async Task ReceiveStripsHeaderAndRejectsUnsupportedPackets()
    {
        var platform = new FakePlatform();
        using var device = new MacOsTunDevice(new(), platform);
        var received = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        device.PacketReceived += (_, args) => received.TrySetResult(args.PacketMemory.ToArray());
        await device.StartAsync();
        platform.Created[0].Incoming.Add(new byte[4]);
        var packet = MacOsTunPacketTests.CreatePacket(8);
        var ipv6 = MacOsTunPacketTests.Frame(packet);
        ipv6[3] = 30;
        platform.Created[0].Incoming.Add(ipv6);
        platform.Created[0].Incoming.Add(MacOsTunPacketTests.Frame(packet));
        Assert.Equal(packet, await received.Task.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task WritesAndBatchesPreservePacketBoundaries()
    {
        var platform = new FakePlatform();
        using var device = new MacOsTunDevice(new(), platform);
        await device.StartAsync();
        var first = MacOsTunPacketTests.CreatePacket(8);
        var second = MacOsTunPacketTests.CreatePacket(12);
        await device.SendAsync(first);
        await device.SendBatchAsync([first, second]);
        var written = platform.Created[0].Written.ToArray();
        Assert.Equal(3, written.Length);
        Assert.Equal(first, written[0]);
        Assert.Equal(first, written[1]);
        Assert.Equal(second, written[2]);
    }

    [Fact]
    public async Task WriteFailureDoesNotRetryOrDuplicateThePacket()
    {
        var platform = new FakePlatform();
        using var device = new MacOsTunDevice(new(), platform);
        await device.StartAsync();
        platform.Created[0].WriteError = new IOException("partial write");
        await Assert.ThrowsAsync<IOException>(async () => await device.SendAsync(MacOsTunPacketTests.CreatePacket(1)));
        Assert.Single(platform.Created[0].Written);
    }

    [Fact]
    public async Task InvalidPacketsAndUnstartedWritesAreRejected()
    {
        using var device = new MacOsTunDevice(new(), new FakePlatform());
        await Assert.ThrowsAsync<ArgumentException>(async () => await device.SendAsync(new byte[2]));
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await device.SendAsync(MacOsTunPacketTests.CreatePacket(0)));
    }

    [Fact]
    public async Task DisposalStopsIdleReadAndCannotRestart()
    {
        var platform = new FakePlatform();
        var device = new MacOsTunDevice(new(), platform);
        await device.StartAsync();
        await Task.Run(device.Dispose).WaitAsync(TimeSpan.FromSeconds(5));
        device.Dispose();
        Assert.False(device.IsStarted);
        Assert.Equal(1, platform.Created[0].DisposeCount);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => device.StartAsync());
        await Assert.ThrowsAsync<ObjectDisposedException>(async () =>
            await device.SendAsync(MacOsTunPacketTests.CreatePacket(0)));
    }

    [Fact]
    public async Task AReceiveFailureMarksDeviceStoppedAndReleasesConnection()
    {
        var platform = new FakePlatform();
        var errors = new ConcurrentQueue<ProxyNetworkerLogEntry>();
        using var device = new MacOsTunDevice(new(), platform, errors.Enqueue);
        await device.StartAsync();
        platform.Created[0].Incoming.Add([]);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (device.IsStarted || Volatile.Read(ref platform.Created[0].DisposeCount) == 0)
        {
            await Task.Delay(10, deadline.Token);
        }

        Assert.Equal(1, platform.Created[0].DisposeCount);
        Assert.NotEmpty(errors);
    }

    private sealed class FakePlatform : IMacOsTunPlatform
    {
        internal List<FakeConnection> Created { get; } = [];
        internal int Configurations { get; private set; }
        internal TunDeviceOptions? LastOptions { get; private set; }
        internal Func<CancellationToken, Task> Configure { get; set; } = _ => Task.CompletedTask;

        public IMacOsTunConnection Create()
        {
            var connection = new FakeConnection();
            Created.Add(connection);
            return connection;
        }

        public Task ConfigureAsync(string name, TunDeviceOptions options, CancellationToken cancellationToken)
        {
            Assert.Equal("utun42", name);
            Configurations++;
            LastOptions = options;
            return Configure(cancellationToken);
        }
    }

    private sealed class FakeConnection : IMacOsTunConnection
    {
        public string Name => "utun42";
        internal BlockingCollection<byte[]> Incoming { get; } = new();
        internal ConcurrentQueue<byte[]> Written { get; } = new();
        internal IOException? WriteError { get; set; }
        internal int DisposeCount;

        public int ReadFrame(Span<byte> buffer, CancellationToken cancellationToken)
        {
            var frame = Incoming.Take(cancellationToken);
            frame.CopyTo(buffer);
            return frame.Length;
        }

        public void WritePacket(ReadOnlySpan<byte> packet, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Written.Enqueue(packet.ToArray());
            if (WriteError is not null)
            {
                throw WriteError;
            }
        }

        public void Dispose() => Interlocked.Increment(ref DisposeCount);
    }
}

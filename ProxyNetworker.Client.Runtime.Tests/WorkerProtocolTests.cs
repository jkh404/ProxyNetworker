using System.Buffers.Binary;
using ProxyNetworker.Client.Runtime;

namespace ProxyNetworker.Client.Runtime.Tests;

public sealed class WorkerProtocolTests
{
    [Fact]
    public async Task MessagesRoundTripWithoutReflection()
    {
        using var stream = new MemoryStream();
        await WorkerProtocol.WriteAsync(stream, new("state", State: ClientState.Stopped), CancellationToken.None);
        stream.Position = 0;
        var read = await WorkerProtocol.ReadAsync(stream, CancellationToken.None);
        Assert.Equal("state", read!.Kind);
        Assert.Equal(ClientConnectionState.Stopped, read.State!.State);
        Assert.Null(await WorkerProtocol.ReadAsync(stream, CancellationToken.None));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(262145)]
    public async Task UntrustedLengthsAreRejectedBeforeAllocation(int length)
    {
        var header = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(header, length);
        using var stream = new MemoryStream(header);
        await Assert.ThrowsAsync<InvalidDataException>(() => WorkerProtocol.ReadAsync(stream, CancellationToken.None));
    }

    [Fact]
    public void AuthenticationRequiresTheWholeSecret()
    {
        Assert.True(WorkerProtocol.Authenticate("a-random-secret", "a-random-secret"));
        Assert.False(WorkerProtocol.Authenticate("a-random-secret", "a-random-secrex"));
        Assert.False(WorkerProtocol.Authenticate("a-random-secret", null));
        Assert.False(WorkerProtocol.Authenticate("a-random-secret", "a"));
    }
}

namespace ProxyNetworker.Core;

internal interface ITunBatchDevice
{
    ValueTask SendBatchAsync(IReadOnlyList<ReadOnlyMemory<byte>> packets, CancellationToken cancellationToken = default);
}

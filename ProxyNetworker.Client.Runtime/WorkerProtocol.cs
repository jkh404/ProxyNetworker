using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ProxyNetworker.Client.Runtime;

/// <summary>Short-lived bootstrap data, stored privately and never passed as a command-line secret.</summary>
public sealed record WorkerBootstrap(string PipeName, string Secret, DateTimeOffset ExpiresAt);

/// <summary>The bounded local worker protocol. No arbitrary commands or file access are exposed.</summary>
public sealed record WorkerMessage(
    string Kind, string? Secret = null, ProxyNetworkerClientConfig? Configuration = null,
    ClientState? State = null, ClientLog? Log = null, string? Error = null);

/// <summary>Length-prefixed, source-generated JSON for authenticated local control only.</summary>
public static class WorkerProtocol
{
    public const int MaximumMessageSize = 256 * 1024;
    public static async Task WriteAsync(Stream stream, WorkerMessage message, CancellationToken cancellationToken)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(message, WorkerJsonContext.Default.WorkerMessage);
        if (bytes.Length > MaximumMessageSize) throw new InvalidDataException("Worker message is too large.");
        var length = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(length, bytes.Length);
        await stream.WriteAsync(length, cancellationToken).ConfigureAwait(false);
        await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }
    public static async Task<WorkerMessage?> ReadAsync(Stream stream, CancellationToken cancellationToken)
    {
        var header = new byte[4];
        var read = await stream.ReadAsync(header.AsMemory(0, 1), cancellationToken).ConfigureAwait(false);
        if (read == 0) return null;
        await stream.ReadExactlyAsync(header.AsMemory(1), cancellationToken).ConfigureAwait(false);
        var length = BinaryPrimitives.ReadInt32LittleEndian(header);
        if (length is <= 0 or > MaximumMessageSize) throw new InvalidDataException("Invalid worker message length.");
        var bytes = new byte[length];
        await stream.ReadExactlyAsync(bytes, cancellationToken).ConfigureAwait(false);
        return JsonSerializer.Deserialize(bytes, WorkerJsonContext.Default.WorkerMessage)
            ?? throw new InvalidDataException("Empty worker message.");
    }
    public static bool Authenticate(string expected, string? actual)
    {
        if (actual is null) return false;
        var first = Encoding.UTF8.GetBytes(expected);
        var second = Encoding.UTF8.GetBytes(actual);
        return first.Length == second.Length && CryptographicOperations.FixedTimeEquals(first, second);
    }
    public static Task<WorkerBootstrap?> ReadBootstrapAsync(Stream stream, CancellationToken cancellationToken) =>
        JsonSerializer.DeserializeAsync(stream, WorkerJsonContext.Default.WorkerBootstrap, cancellationToken).AsTask();
    public static Task WriteBootstrapAsync(Stream stream, WorkerBootstrap bootstrap, CancellationToken cancellationToken) =>
        JsonSerializer.SerializeAsync(stream, bootstrap, WorkerJsonContext.Default.WorkerBootstrap, cancellationToken);
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(WorkerBootstrap))]
[JsonSerializable(typeof(WorkerMessage))]
internal sealed partial class WorkerJsonContext : JsonSerializerContext;

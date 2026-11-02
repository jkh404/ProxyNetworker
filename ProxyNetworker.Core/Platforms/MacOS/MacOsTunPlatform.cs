namespace ProxyNetworker.Core.Platforms.MacOS;

internal interface IMacOsTunConnection : IDisposable
{
    string Name { get; }

    int ReadFrame(Span<byte> buffer, CancellationToken cancellationToken);

    void WritePacket(ReadOnlySpan<byte> packet, CancellationToken cancellationToken);
}

internal interface IMacOsTunPlatform
{
    IMacOsTunConnection Create();

    Task ConfigureAsync(string name, TunDeviceOptions options, CancellationToken cancellationToken);
}

internal sealed class MacOsTunPlatform : IMacOsTunPlatform
{
    internal static readonly MacOsTunPlatform Instance = new();

    public IMacOsTunConnection Create() => MacOsTunConnection.Create();

    public async Task ConfigureAsync(string name, TunDeviceOptions options, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        MacOsTunConfiguration.RejectConflictingInterfaces(name, options);
        foreach (var command in MacOsTunConfiguration.CreateCommands(name, options))
        {
            await MacOsTunConfiguration.RunCommandAsync(command, cancellationToken).ConfigureAwait(false);
        }
    }
}

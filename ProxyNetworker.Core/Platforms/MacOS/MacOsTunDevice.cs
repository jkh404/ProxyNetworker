namespace ProxyNetworker.Core.Platforms.MacOS;

internal sealed class MacOsTunDevice : ITunDevice, ITunBatchDevice
{
    private readonly TunDeviceOptions _options;
    private readonly IMacOsTunPlatform _platform;
    private readonly Action<ProxyNetworkerLogEntry>? _logger;
    private readonly SemaphoreSlim _lifecycleGate = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();
    private CancellationTokenSource? _runtimeCancellation;
    private IMacOsTunConnection? _connection;
    private Task? _readLoop;
    private int _started;
    private int _disposed;

    internal MacOsTunDevice(TunDeviceOptions options, Action<ProxyNetworkerLogEntry>? logger = null)
        : this(options, MacOsTunPlatform.Instance, logger)
    {
    }

    internal MacOsTunDevice(
        TunDeviceOptions options,
        IMacOsTunPlatform platform,
        Action<ProxyNetworkerLogEntry>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(platform);
        MacOsTunConfiguration.Validate(options);
        _options = new TunDeviceOptions
        {
            Name = options.Name,
            TunnelType = options.TunnelType,
            Address = options.Address,
            PrefixLength = options.PrefixLength,
            Mtu = options.Mtu,
            ConfigureInterface = options.ConfigureInterface
        };
        _platform = platform;
        _logger = logger;
    }

    public string Name { get; private set; } = "utun";

    public bool IsStarted => Volatile.Read(ref _started) != 0;

    public event EventHandler<TunPacketReceivedEventArgs>? PacketReceived;

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        var runtime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
        var acquired = false;
        var committed = false;
        IMacOsTunConnection? connection = null;
        try
        {
            await _lifecycleGate.WaitAsync(runtime.Token).ConfigureAwait(false);
            acquired = true;
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            if (IsStarted)
            {
                return;
            }

            connection = _platform.Create();
            if (!MacOsTunConfiguration.IsInterfaceName(connection.Name))
            {
                throw new IOException("The macOS backend returned an invalid interface name.");
            }

            Name = connection.Name;
            if (_options.ConfigureInterface)
            {
                await _platform.ConfigureAsync(Name, _options, runtime.Token).ConfigureAwait(false);
            }

            runtime.Token.ThrowIfCancellationRequested();
            Log(ProxyNetworkerLogLevel.Information,
                $"Started macOS utun device '{Name}' for '{_options.Name}' with {_options.Address}/{_options.PrefixLength}, mtu={_options.Mtu}; ordinary packet I/O.");
            _runtimeCancellation?.Dispose();
            _runtimeCancellation = runtime;
            Volatile.Write(ref _connection, connection);
            Interlocked.Exchange(ref _started, 1);
            var activeConnection = connection;
            _readLoop = Task.Run(() => ReadLoop(activeConnection, runtime.Token));
            committed = true;
        }
        finally
        {
            if (!committed)
            {
                // Only the newly allocated utun is detached; no pre-existing route is deleted.
                connection?.Dispose();
                runtime.Dispose();
            }

            if (acquired)
            {
                _lifecycleGate.Release();
            }
        }
    }

    public ValueTask SendAsync(ReadOnlyMemory<byte> packet, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (!MacOsUtunPacket.IsValidIpv4Packet(packet.Span))
        {
            throw new ArgumentException("utun requires a complete IPv4 packet with a matching total length.", nameof(packet));
        }

        var connection = Volatile.Read(ref _connection);
        if (!IsStarted || connection is null)
        {
            throw new InvalidOperationException("The macOS utun device has not been started.");
        }

        connection.WritePacket(packet.Span, cancellationToken);
        return ValueTask.CompletedTask;
    }

    public async ValueTask SendBatchAsync(
        IReadOnlyList<ReadOnlyMemory<byte>> packets,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(packets);
        foreach (var packet in packets)
        {
            await SendAsync(packet, cancellationToken).ConfigureAwait(false);
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _lifetime.Cancel();
        _lifecycleGate.Wait();
        try
        {
            Interlocked.Exchange(ref _started, 0);
            _runtimeCancellation?.Cancel();
            Interlocked.Exchange(ref _connection, null)?.Dispose();
            if (_readLoop is not null && Task.CurrentId != _readLoop.Id)
            {
                _readLoop.GetAwaiter().GetResult();
            }

            _runtimeCancellation?.Dispose();
            _lifetime.Dispose();
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    private void ReadLoop(IMacOsTunConnection connection, CancellationToken cancellationToken)
    {
        var buffer = GC.AllocateUninitializedArray<byte>(
            MacOsUtunPacket.MaximumPacketSize + MacOsUtunPacket.HeaderSize);
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var length = connection.ReadFrame(buffer, cancellationToken);
                if (length <= 0 || length > buffer.Length)
                {
                    throw new IOException("The macOS utun control socket stopped returning packets.");
                }

                if (!MacOsUtunPacket.TryGetPacket(buffer.AsMemory(0, length), out var packet))
                {
                    continue;
                }

                // Subscribers consume this view before the next read reuses the buffer.
                PacketReceived?.Invoke(this, new TunPacketReceivedEventArgs(packet));
            }
        }
        catch (Exception ex) when (cancellationToken.IsCancellationRequested &&
            ex is OperationCanceledException or ObjectDisposedException or IOException)
        {
        }
        catch (Exception ex)
        {
            Log(ProxyNetworkerLogLevel.Error, $"macOS utun read loop failed: {ex.Message}");
        }
        finally
        {
            if (ReferenceEquals(Interlocked.CompareExchange(ref _connection, null, connection), connection))
            {
                Interlocked.Exchange(ref _started, 0);
                connection.Dispose();
            }
        }
    }

    private void Log(ProxyNetworkerLogLevel level, string message) =>
        _logger?.Invoke(new ProxyNetworkerLogEntry(level, message));
}

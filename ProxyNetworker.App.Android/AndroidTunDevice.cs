using System.Buffers.Binary;
using System.Net;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using Android.Net;
using ProxyNetworker.Core;

namespace ProxyNetworker.App.Android;

/// <summary>Owns the nonblocking raw IPv4 TUN descriptor supplied by Android VpnService.</summary>
internal sealed class AndroidTunDevice(
    NetworkVpnService service, TunDeviceOptions options, Action<ProxyNetworkerLogEntry> logger) : ITunDevice
{
    private readonly CancellationTokenSource _stop = new();
    private readonly object _writeGate = new();
    private SafeTunHandle? _handle;
    private Task? _read;
    private int _started, _disposed;
    public string Name => "Android VPN";
    public bool IsStarted => Volatile.Read(ref _started) != 0;
    public event EventHandler<TunPacketReceivedEventArgs>? PacketReceived;

    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        options.Validate();
        if (options.PrefixLength is < 1 or > 32) throw new ArgumentException("Only a virtual IPv4 subnet may be routed.");
        if (Interlocked.Exchange(ref _started, 1) != 0) return Task.CompletedTask;
        try
        {
            var bytes = options.Address.GetAddressBytes();
            var address = BinaryPrimitives.ReadUInt32BigEndian(bytes);
            var network = address & (uint.MaxValue << (32 - options.PrefixLength));
            BinaryPrimitives.WriteUInt32BigEndian(bytes, network);
            using var builder = new VpnService.Builder(service);
            builder.SetSession("ProxyNetworker").SetMtu(options.Mtu)
                .AddAddress(options.Address.ToString(), options.PrefixLength)
                .AddRoute(new IPAddress(bytes).ToString(), options.PrefixLength)
                .SetBlocking(false);
            // Control HTTP, port-tunnel sockets and DNS must never loop back into this VPN.
            builder.AddDisallowedApplication(service.PackageName!);
            using var descriptor = builder.Establish() ?? throw new IOException("Android did not create the VPN interface.");
            _handle = new SafeTunHandle(descriptor.DetachFd());
            _read = Task.Run(ReadLoop);
            logger(new(ProxyNetworkerLogLevel.Information, $"Android VPN started: {options.Address}/{options.PrefixLength}, mtu={options.Mtu}."));
            return Task.CompletedTask;
        }
        catch { Interlocked.Exchange(ref _started, 0); _handle?.Dispose(); throw; }
    }

    private unsafe void ReadLoop()
    {
        var buffer = new byte[65535];
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                var reference = false;
                try
                {
                    var handle = _handle ?? throw new IOException("VPN descriptor missing.");
                    handle.DangerousAddRef(ref reference);
                    var descriptor = checked((int)handle.DangerousGetHandle());
                    var poll = new AndroidTunNative.Poll { Descriptor = descriptor, Events = 1 };
                    var ready = AndroidTunNative.PollSocket(ref poll, 1, 100);
                    if (ready == 0) continue;
                    if (ready < 0 && Marshal.GetLastPInvokeError() == 4) continue;
                    if (ready < 0 || (poll.Returned & 0x38) != 0) throw new IOException("VPN descriptor poll failed.");
                    nint length;
                    fixed (byte* pointer = buffer) length = AndroidTunNative.Read(descriptor, pointer, (nuint)buffer.Length);
                    if (length < 0 && Marshal.GetLastPInvokeError() is 4 or 11) continue;
                    if (length <= 0) throw new IOException("Android VPN read failed.");
                    PacketReceived?.Invoke(this, new(buffer.AsMemory(0, checked((int)length))));
                }
                finally { if (reference) _handle!.DangerousRelease(); }
            }
        }
        catch (Exception exception)
        {
            if (!_stop.IsCancellationRequested)
                logger(new(ProxyNetworkerLogLevel.Error, "Android VPN read stopped.", exception));
        }
        finally { Interlocked.Exchange(ref _started, 0); _handle?.Dispose(); }
    }

    public unsafe ValueTask SendAsync(ReadOnlyMemory<byte> packet, CancellationToken cancellationToken = default)
    {
        lock (_writeGate)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!IsStarted || _handle is null) throw new IOException("Android VPN is stopped.");
            var reference = false;
            try
            {
                _handle.DangerousAddRef(ref reference);
                var descriptor = checked((int)_handle.DangerousGetHandle());
                while (true)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    _stop.Token.ThrowIfCancellationRequested();
                    nint written;
                    fixed (byte* pointer = packet.Span) written = AndroidTunNative.Write(descriptor, pointer, (nuint)packet.Length);
                    if (written == packet.Length) return ValueTask.CompletedTask;
                    if (written >= 0) throw new IOException("Partial VPN packet write; packet was not retried.");
                    var error = Marshal.GetLastPInvokeError();
                    if (error == 4) continue;
                    if (error != 11) throw new IOException($"Android VPN write failed: errno={error}.");
                    var poll = new AndroidTunNative.Poll { Descriptor = descriptor, Events = 4 };
                    if (AndroidTunNative.PollSocket(ref poll, 1, 100) < 0 && Marshal.GetLastPInvokeError() != 4)
                        throw new IOException("Android VPN write poll failed.");
                }
            }
            finally { if (reference) _handle.DangerousRelease(); }
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _stop.Cancel();
        if (_read is not null && Task.CurrentId != _read.Id) _read.GetAwaiter().GetResult();
        _handle?.Dispose(); _stop.Dispose(); Interlocked.Exchange(ref _started, 0);
    }
    private sealed class SafeTunHandle : SafeHandleMinusOneIsInvalid
    {
        internal SafeTunHandle(int descriptor) : base(true) { SetHandle(descriptor); }
        protected override bool ReleaseHandle() => AndroidTunNative.Close(checked((int)handle)) == 0;
    }
}

internal static partial class AndroidTunNative
{
    [StructLayout(LayoutKind.Sequential)]
    internal struct Poll { internal int Descriptor; internal short Events; internal short Returned; }
    [LibraryImport("libc", EntryPoint = "poll", SetLastError = true)]
    internal static partial int PollSocket(ref Poll poll, nuint count, int timeout);
    [LibraryImport("libc", EntryPoint = "read", SetLastError = true)]
    internal static unsafe partial nint Read(int descriptor, byte* buffer, nuint length);
    [LibraryImport("libc", EntryPoint = "write", SetLastError = true)]
    internal static unsafe partial nint Write(int descriptor, byte* buffer, nuint length);
    [LibraryImport("libc", EntryPoint = "close")]
    internal static partial int Close(int descriptor);
}

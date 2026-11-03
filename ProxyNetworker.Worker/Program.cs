using System.IO.Pipes;
using System.Threading.Channels;
using ProxyNetworker.Client.Runtime;

if (args is not ["--bootstrap", var bootstrapPath])
{
    Console.Error.WriteLine("This network worker is launched by the desktop client.");
    return 2;
}

using var lifetime = new CancellationTokenSource();
try
{
    if (!OperatingSystem.IsWindows() &&
        (File.GetUnixFileMode(bootstrapPath) & (UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.OtherRead | UnixFileMode.OtherWrite)) != 0)
        throw new IOException("Worker bootstrap permissions are not private.");
    WorkerBootstrap bootstrap;
    await using (var stream = File.OpenRead(bootstrapPath))
        bootstrap = await WorkerProtocol.ReadBootstrapAsync(stream, lifetime.Token)
            ?? throw new IOException("Missing worker bootstrap.");
    if (bootstrap.ExpiresAt < DateTimeOffset.UtcNow || bootstrap.ExpiresAt > DateTimeOffset.UtcNow.AddMinutes(3) ||
        !bootstrap.PipeName.StartsWith("proxynetworker-", StringComparison.Ordinal) ||
        bootstrap.PipeName.Length != 47 || Convert.FromBase64String(bootstrap.Secret).Length != 32)
        throw new IOException("Invalid or expired worker bootstrap.");
    using var pipe = new NamedPipeClientStream(".", bootstrap.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
    await pipe.ConnectAsync(30000, lifetime.Token);
    await WorkerProtocol.WriteAsync(pipe, new("hello", Secret: bootstrap.Secret), lifetime.Token);
    var first = await WorkerProtocol.ReadAsync(pipe, lifetime.Token);
    if (first is not { Kind: "start", Configuration: { } configuration })
        throw new IOException("Expected an authenticated start request.");
    ClientConfigurationStore.Validate(configuration);

    var messages = Channel.CreateBounded<WorkerMessage>(new BoundedChannelOptions(128)
    {
        SingleReader = true, SingleWriter = false, FullMode = BoundedChannelFullMode.DropOldest
    });
    await using var controller = new LocalClientController(new DesktopClientPlatform());
    controller.StateChanged += state => messages.Writer.TryWrite(new("state", State: state));
    controller.LogReceived += log => messages.Writer.TryWrite(new("log", Log: log));
    var writer = Task.Run(async () =>
    {
        await foreach (var message in messages.Reader.ReadAllAsync(lifetime.Token))
            await WorkerProtocol.WriteAsync(pipe, message, lifetime.Token);
    });
    try
    {
        await controller.ConnectAsync(configuration, lifetime.Token);
        while (await WorkerProtocol.ReadAsync(pipe, lifetime.Token) is { } request)
        {
            if (request.Kind != "stop") throw new IOException("Unsupported worker command.");
            break;
        }
    }
    finally
    {
        await controller.DisconnectAsync(CancellationToken.None);
        messages.Writer.TryComplete();
        lifetime.Cancel();
        try { await writer; } catch (Exception exception) when (exception is IOException or OperationCanceledException) { }
    }
    return 0;
}
catch (Exception exception)
{
    Console.Error.WriteLine("Network worker failed: " + exception.GetType().Name);
    return 1;
}

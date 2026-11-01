using System.Text.Json;
using Serilog;
using Serilog.Events;

Log.Logger = CreateLogger();

try
{
    return await ProxyNetworkerClientApp.RunAsync(CommandLine.Parse(args));
}
catch (Exception ex)
{
    Log.Fatal(ex, "ProxyNetworker.Client terminated unexpectedly.");
    Console.Error.WriteLine(ex.Message);
    return 1;
}
finally
{
    await Log.CloseAndFlushAsync();
}

static Serilog.ILogger CreateLogger()
{
    var logPath = Path.Combine(AppContext.BaseDirectory, "logs", "client-.log");
    var logDirectory = Path.GetDirectoryName(logPath);
    if (!string.IsNullOrWhiteSpace(logDirectory))
    {
        Directory.CreateDirectory(logDirectory);
    }

    return new LoggerConfiguration()
        .MinimumLevel.Is(ReadMinimumLogLevel())
        .MinimumLevel.Override("Microsoft", LogEventLevel.Warning)
        .MinimumLevel.Override("System", LogEventLevel.Warning)
        .Enrich.FromLogContext()
        .Enrich.WithProperty("Application", "ProxyNetworker.Client")
        .WriteTo.Console()
        .WriteTo.File(
            logPath,
            rollingInterval: RollingInterval.Day,
            retainedFileCountLimit: 14,
            shared: true)
        .CreateLogger();
}

static LogEventLevel ReadMinimumLogLevel()
{
    var currentDirectory = Directory.GetCurrentDirectory();
    var candidates = new[]
    {
        Path.Combine(currentDirectory, "appsettings.json"),
        Path.Combine(currentDirectory, "proxynetworker.client.json"),
        Path.Combine(AppContext.BaseDirectory, "appsettings.json"),
        Path.Combine(AppContext.BaseDirectory, "proxynetworker.client.json")
    };

    var path = candidates.Distinct(StringComparer.OrdinalIgnoreCase).FirstOrDefault(File.Exists);
    if (path is null)
    {
        return LogEventLevel.Information;
    }

    try
    {
        using var stream = File.OpenRead(path);
        using var document = JsonDocument.Parse(stream);
        if (!document.RootElement.TryGetProperty("Serilog", out var serilog) ||
            !serilog.TryGetProperty("MinimumLevel", out var minimumLevel))
        {
            return LogEventLevel.Information;
        }

        var value = minimumLevel.ValueKind == JsonValueKind.Object &&
            minimumLevel.TryGetProperty("Default", out var defaultLevel)
                ? defaultLevel.GetString()
                : minimumLevel.GetString();

        return Enum.TryParse<LogEventLevel>(value, ignoreCase: true, out var parsed)
            ? parsed
            : LogEventLevel.Information;
    }
    catch (JsonException)
    {
        return LogEventLevel.Information;
    }
    catch (IOException)
    {
        return LogEventLevel.Information;
    }
}

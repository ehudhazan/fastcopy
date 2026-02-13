/*

# Windows
dotnet publish FastCopy.csproj -r win-x64 -c Release -p:PublishAot=true -o ./publish/win-x64

# Linux (Debian/Fedora)
dotnet publish FastCopy.csproj -r linux-x64 -c Release -p:PublishAot=true -o ./publish/linux-x64

# Alpine
dotnet publish FastCopy.csproj -r linux-musl-x64 -c Release -p:PublishAot=true -o ./publish/alpine-x64

*/

using FastCopy.Processors;
using FastCopy.Services;
using Terminal.Gui;
using FastCopy.UI;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Hosting;
using System.Diagnostics;
using FastCopy.Core;

using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

if (Avx2.IsSupported)
{
    // The app will use 256-bit wide instructions for hashing and copying
    Console.WriteLine("Hardware Acceleration: AVX2 Active");
}
else if (Sse41.IsSupported)
{
    Console.WriteLine("Hardware Acceleration: SSE4.1 Active");
}
else
{
    Console.WriteLine("Hardware Acceleration: Not Supported (Fallback to Scalar)");
}
if (args.Contains("--serve"))
{
    var builder = WebApplication.CreateSlimBuilder(args);

    builder.Logging.ClearProviders();
    builder.Logging.AddConsole();

    // Configure graceful shutdown
    builder.Services.Configure<HostOptions>(options =>
    {
        options.ShutdownTimeout = TimeSpan.FromSeconds(10);
    });

    builder.Services.AddGrpc();

    var app = builder.Build();

    // Register shutdown handler to flush any services
    var lifetime = app.Services.GetRequiredService<IHostApplicationLifetime>();
    lifetime.ApplicationStopping.Register(() =>
    {
        Console.WriteLine("Graceful shutdown initiated...");
        // Add any service flush logic here if needed
    });

    app.MapGrpcService<GrpcFileTransferService>();
    app.MapGet("/", () => "Use a gRPC client to communicate with this server.");

    Console.WriteLine("Starting FastCopy gRPC Server...");
    await app.RunAsync();
    return;
}

// Parse resource management flags
long maxMemoryMB = 0;
bool lowPriority = false;
long rateLimitBytesPerSec = 0; // 0 = no limit

for (int i = 0; i < args.Length; i++)
{
    if (args[i] == "--max-mem" && i + 1 < args.Length)
    {
        if (long.TryParse(args[i + 1], out long parsedMem) && parsedMem > 0)
        {
            maxMemoryMB = parsedMem;
        }
        else
        {
            Console.Error.WriteLine("Error: --max-mem must be a positive integer (MB).");
            return;
        }
        i++;
    }
    else if (args[i] == "--low-priority")
    {
        lowPriority = true;
    }
    else if (args[i] == "--limit" && i + 1 < args.Length)
    {
        // Parse human-readable byte size (e.g., "100MB", "1GB")
        if (ByteSizeParser.TryParse(args[i + 1], out long parsedLimit) && parsedLimit >= 0)
        {
            rateLimitBytesPerSec = parsedLimit;
        }
        else
        {
            Console.Error.WriteLine($"Error: --limit must be a valid size (e.g., 100MB, 1GB). Got: '{args[i + 1]}'");
            return;
        }
        i++;
    }
}

// Set global rate limit if specified
if (rateLimitBytesPerSec > 0)
{
    TransferEngine.SetGlobalLimit(rateLimitBytesPerSec);
    Console.WriteLine($"Global rate limit set to {rateLimitBytesPerSec:N0} bytes/sec.");
}

// Set process priority if requested
if (lowPriority)
{
    try
    {
        Process.GetCurrentProcess().PriorityClass = ProcessPriorityClass.BelowNormal;
        Console.WriteLine("Process priority set to BelowNormal.");
    }
    catch (Exception ex)
    {
        Console.WriteLine($"Warning: Failed to set process priority: {ex.Message}");
    }
}

// Check for --quiet flag for headless mode
bool quietMode = args.Contains("--quiet");

if (args.Length == 0 || quietMode)
{
    // Launch Dashboard (TUI or headless mode)
#pragma warning disable IL2026, IL3050 // Terminal.Gui is not fully AOT compatible yet

    // Initialize ResourceWatchdog
    var resourceWatchdog = new ResourceWatchdog(
        initialMaxThreads: Environment.ProcessorCount,
        maxMemoryMB: maxMemoryMB,
        statsCallback: null // Dashboard will read LatestStats directly
    );

    if (quietMode)
    {
        // Headless mode - no Terminal.Gui, just console output
        var dashboard = new Dashboard(showUI: false, resourceWatchdog: resourceWatchdog);
        Console.WriteLine("FastCopy running in headless mode. Status updates every 5 seconds...");
        Console.WriteLine("Press Ctrl+C to exit.");

        var cts = new CancellationTokenSource();
        var shutdownComplete = new TaskCompletionSource();

        Console.CancelKeyPress += (s, e) =>
        {
            e.Cancel = true; // Prevent immediate termination
            Console.WriteLine("\nShutdown signal received. Cleaning up...");

            if (!cts.IsCancellationRequested)
            {
                cts.Cancel();
                // Signal shutdown completion after cleanup
                shutdownComplete.TrySetResult();
            }
        };

        try
        {
            await Task.Delay(System.Threading.Timeout.Infinite, cts.Token);
        }
        catch (TaskCanceledException)
        {
            // Graceful shutdown initiated
        }
        finally
        {
            // Ensure all resources are cleaned up
            resourceWatchdog.Dispose();
            Console.WriteLine("Shutdown complete.");
        }
    }
    else
    {
        // Full TUI mode
        Application.Init();
        var dashboard = new Dashboard(showUI: true, resourceWatchdog: resourceWatchdog);
        Application.Run(dashboard);
        Application.Shutdown();
        resourceWatchdog.Dispose();
    }

#pragma warning restore IL2026, IL3050
    return;
}

string? fileListPath = null;
int retries = 2; // Default retry count

for (int i = 0; i < args.Length; i++)
{
    if (args[i] == "--file-list" && i + 1 < args.Length)
    {
        fileListPath = args[i + 1];
        i++;
    }
    else if (args[i] == "--retries" && i + 1 < args.Length)
    {
        if (int.TryParse(args[i + 1], out int parsedRetries) && parsedRetries >= 0)
        {
            retries = parsedRetries;
        }
        else
        {
            Console.Error.WriteLine("Error: --retries must be a non-negative integer.");
            return;
        }
        i++;
    }
}

if (string.IsNullOrEmpty(fileListPath))
{
    Console.WriteLine("Error: --file-list argument is required.");
    return;
}

Console.WriteLine($"Starting batch processing for: {fileListPath}");

// Set up cancellation for graceful shutdown
var batchCts = new CancellationTokenSource();
Console.CancelKeyPress += (s, e) =>
{
    e.Cancel = true; // Prevent immediate termination
    Console.WriteLine("\nShutdown signal received. Stopping batch processing...");
    batchCts.Cancel();
};

try
{
    var processor = new FastCopy.Processors.BatchProcessor();
    // Default concurrency to ProcessorCount
    await processor.ProcessAsync(fileListPath, concurrency: Environment.ProcessorCount, cancellationToken: batchCts.Token);
    Console.WriteLine("Batch processing completed successfully.");
}
catch (OperationCanceledException)
{
    Console.WriteLine("Batch processing was cancelled.");
    Environment.Exit(130); // Standard exit code for SIGINT
}
catch (Exception ex)
{
    Console.Error.WriteLine($"Critical error: {ex.Message}");
    Environment.Exit(1);
}
finally
{
    batchCts.Dispose();
}

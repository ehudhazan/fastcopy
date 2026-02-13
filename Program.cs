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

if (args.Contains("--serve"))
{
    var builder = WebApplication.CreateSlimBuilder(args);
    
    builder.Logging.ClearProviders();
    builder.Logging.AddConsole();
    
    builder.Services.AddGrpc();
    
    var app = builder.Build();
    
    app.MapGrpcService<GrpcFileTransferService>();
    app.MapGet("/", () => "Use a gRPC client to communicate with this server.");
    
    Console.WriteLine("Starting FastCopy gRPC Server...");
    await app.RunAsync();
    return;
}

// Parse resource management flags
long maxMemoryMB = 0;
bool lowPriority = false;

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
        Console.CancelKeyPress += (s, e) =>
        {
            e.Cancel = true;
            cts.Cancel();
        };
        
        try
        {
            await Task.Delay(System.Threading.Timeout.Infinite, cts.Token);
        }
        catch (TaskCanceledException)
        {
            // Clean exit
        }
        finally
        {
            resourceWatchdog.Dispose();
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

try
{
    var processor = new FastCopy.Processors.BatchProcessor();
    // Default concurrency to ProcessorCount
    await processor.ProcessAsync(fileListPath, concurrency: Environment.ProcessorCount);
    Console.WriteLine("Batch processing completed successfully.");
}
catch (Exception ex)
{
    Console.Error.WriteLine($"Critical error: {ex.Message}");
    Environment.Exit(1);
}

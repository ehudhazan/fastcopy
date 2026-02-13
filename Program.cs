using FastCopy.Processors;
using FastCopy.Services;
using Terminal.Gui;
using FastCopy.UI;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Hosting;

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

if (args.Length == 0)
{
    // Launch TUI Dashboard
#pragma warning disable IL2026, IL3050 // Terminal.Gui is not fully AOT compatible yet
    Application.Init();
    Application.Run(new Dashboard());
    Application.Shutdown();
#pragma warning restore IL2026, IL3050
    return;
}

string? fileListPath = null;

for (int i = 0; i < args.Length; i++)
{
    if (args[i] == "--file-list" && i + 1 < args.Length)
    {
        fileListPath = args[i + 1];
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
    var processor = new BatchProcessor();
    // Default concurrency to ProcessorCount
    await processor.ProcessAsync(fileListPath, concurrency: Environment.ProcessorCount);
    Console.WriteLine("Batch processing completed successfully.");
}
catch (Exception ex)
{
    Console.Error.WriteLine($"Critical error: {ex.Message}");
    Environment.Exit(1);
}

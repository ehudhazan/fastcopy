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
using FastCopy.UI;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Hosting;
using System.Diagnostics;
using FastCopy.Core;
using System.CommandLine;
using System.Threading.Channels;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;
using Spectre.Console;

if (Avx2.IsSupported)
{
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

// Demo mode for showcasing the Spectre.Console TUI dashboard
if (args.Contains("--demo-dashboard"))
{
    var cts = new CancellationTokenSource();
    Console.CancelKeyPress += (s, e) =>
    {
        e.Cancel = true;
        cts.Cancel();
    };
    
    await RunSpectreConsoleDashboardDemoAsync(cts.Token);
    return;
}

if (args.Contains("--serve"))
{
    var builder = WebApplication.CreateSlimBuilder(args);

    builder.Logging.ClearProviders();
    builder.Logging.AddConsole();

    builder.Services.Configure<HostOptions>(options =>
    {
        options.ShutdownTimeout = TimeSpan.FromSeconds(10);
    });

    builder.Services.AddGrpc();

    var app = builder.Build();

    var lifetime = app.Services.GetRequiredService<IHostApplicationLifetime>();
    lifetime.ApplicationStopping.Register(() =>
    {
        Console.WriteLine("Graceful shutdown initiated...");
    });

    app.MapGrpcService<GrpcFileTransferService>();
    app.MapGet("/", () => "Use a gRPC client to communicate with this server.");

    Console.WriteLine("Starting FastCopy gRPC Server...");
    await app.RunAsync();
    return;
}

// Smart Fallback: If no arguments provided, launch interactive menu
if (args.Length == 0)
{
    await RunInteractiveMenuAsync();
    return;
}

// Build System.CommandLine configuration
var rootCommand = new RootCommand("FastCopy - High-performance file copy utility");

// Core copy options
var srcOption = new Option<string?>(
    aliases: new[] { "--src", "-s" },
    description: "Source path (file or directory)");

var dstOption = new Option<string?>(
    aliases: new[] { "--dst", "-d" },
    description: "Destination path");

var limitOption = new Option<string?>(
    aliases: new[] { "--limit", "-l" },
    description: "Global speed limit per second for all files combined (e.g., 50MB, 1GB/s, 500KB). Supports units: KB, MB, GB, TB");

var verifyOption = new Option<bool>(
    aliases: new[] { "--verify" },
    description: "Verify file integrity after copy using XXH3 checksum algorithm",
    getDefaultValue: () => false);

var dryRunOption = new Option<bool>(
    aliases: new[] { "--dry-run" },
    description: "Simulate without copying",
    getDefaultValue: () => false);

var deleteOption = new Option<bool>(
    aliases: new[] { "--delete" },
    description: "Delete source after successful copy",
    getDefaultValue: () => false);

var onCompleteOption = new Option<string?>(
    aliases: new[] { "--on-complete" },
    description: "Command to execute on completion");

var retriesOption = new Option<int>(
    aliases: new[] { "--retries" },
    description: "Maximum number of retries",
    getDefaultValue: () => 2);

var quietOption = new Option<bool>(
    aliases: new[] { "--quiet", "-q" },
    description: "Run in headless mode without TUI",
    getDefaultValue: () => false);

var retryFailedOption = new Option<string?>(
    aliases: new[] { "--retry-failed" },
    description: "Retry failed jobs from JSONL file");

// Legacy options (preserved for backward compatibility)
var maxMemOption = new Option<long?>(
    aliases: new[] { "--max-mem" },
    description: "Maximum memory in MB");

var lowPriorityOption = new Option<bool>(
    aliases: new[] { "--low-priority" },
    description: "Set process to low priority",
    getDefaultValue: () => false);

var fileListOption = new Option<string?>(
    aliases: new[] { "--file-list" },
    description: "Path to file containing list of files to copy. Format: one 'source|destination' pair per line");

var parallelOption = new Option<int>(
    aliases: new[] { "--parallel", "-j" },
    description: "Number of files to process in parallel (default: CPU core count)",
    getDefaultValue: () => Environment.ProcessorCount);

rootCommand.AddOption(srcOption);
rootCommand.AddOption(dstOption);
rootCommand.AddOption(limitOption);
rootCommand.AddOption(verifyOption);
rootCommand.AddOption(dryRunOption);
rootCommand.AddOption(deleteOption);
rootCommand.AddOption(onCompleteOption);
rootCommand.AddOption(retriesOption);
rootCommand.AddOption(quietOption);
rootCommand.AddOption(retryFailedOption);
rootCommand.AddOption(maxMemOption);
rootCommand.AddOption(lowPriorityOption);
rootCommand.AddOption(fileListOption);
rootCommand.AddOption(parallelOption);

rootCommand.SetHandler(async (context) =>
{
    var src = context.ParseResult.GetValueForOption(srcOption);
    var dst = context.ParseResult.GetValueForOption(dstOption);
    var limitStr = context.ParseResult.GetValueForOption(limitOption);
    var verify = context.ParseResult.GetValueForOption(verifyOption);
    var dryRun = context.ParseResult.GetValueForOption(dryRunOption);
    var delete = context.ParseResult.GetValueForOption(deleteOption);
    var onComplete = context.ParseResult.GetValueForOption(onCompleteOption);
    var retries = context.ParseResult.GetValueForOption(retriesOption);
    var quiet = context.ParseResult.GetValueForOption(quietOption);
    var retryFailed = context.ParseResult.GetValueForOption(retryFailedOption);
    var maxMemMB = context.ParseResult.GetValueForOption(maxMemOption);
    var lowPriority = context.ParseResult.GetValueForOption(lowPriorityOption);
    var fileListPath = context.ParseResult.GetValueForOption(fileListOption);
    var parallel = context.ParseResult.GetValueForOption(parallelOption);
    
    var cancellationToken = context.GetCancellationToken();
    
    await ExecuteFastCopyAsync(
        src, dst, limitStr, verify, dryRun, delete, onComplete, retries, quiet,
        retryFailed, maxMemMB, lowPriority, fileListPath, parallel, cancellationToken);
});

await rootCommand.InvokeAsync(args);

/// <summary>
/// Run the interactive menu for configuring and launching a copy operation.
/// Native Console-based UI with Zero-GC rendering.
/// </summary>
static async Task RunInteractiveMenuAsync()
{
    using var consoleInterface = new ConsoleInterface();
    
    var cts = new CancellationTokenSource();
    Console.CancelKeyPress += (s, e) =>
    {
        e.Cancel = true;
        cts.Cancel();
    };
    
    // Launch menu
    await consoleInterface.RunMenuAsync(cts.Token);
    
    // Check if user wants to start a copy
    var menuState = consoleInterface.GetMenuState();
    
    if (menuState.ShouldStart && !string.IsNullOrEmpty(menuState.SourcePath) && !string.IsNullOrEmpty(menuState.DestinationPath))
    {
        // Parse speed limit
        string? limitStr = menuState.SpeedLimitText == "Unlimited" ? null : menuState.SpeedLimitText;
        
        // Execute the copy operation
        await CopyOperationHelper.ExecuteAsync(
            src: menuState.SourcePath,
            dst: menuState.DestinationPath,
            limitStr: limitStr,
            verify: menuState.VerifyChecksum,
            dryRun: false,
            delete: false,
            onComplete: null,
            retries: 2,
            quiet: false,
            retryFailed: null,
            maxMemMB: null,
            lowPriority: false,
            fileListPath: null,
            parallel: menuState.MaxParallelism,
            cancellationToken: cts.Token);
    }
}

static async Task ExecuteFastCopyAsync(
    string? src,
    string? dst,
    string? limitStr,
    bool verify,
    bool dryRun,
    bool delete,
    string? onComplete,
    int retries,
    bool quiet,
    string? retryFailed,
    long? maxMemMB,
    bool lowPriority,
    string? fileListPath,
    int parallel,
    CancellationToken cancellationToken)
{
    await CopyOperationHelper.ExecuteAsync(
        src, dst, limitStr, verify, dryRun, delete, onComplete, retries, quiet,
        retryFailed, maxMemMB, lowPriority, fileListPath, parallel, cancellationToken);
}

/// <summary>
/// Demo mode showcasing the Spectre.Console Interactive TUI dashboard with NativeAOT support.
/// </summary>
static async Task RunSpectreConsoleDashboardDemoAsync(CancellationToken cancellationToken)
{
    // Create ViewModel and pause token source
    var viewModel = new DashboardViewModel();
    var pauseTokenSource = new PauseTokenSource();
    
    // Create mock workers for demo
    var mockWorkers = new List<WorkerState>();
    for (int i = 0; i < 15; i++)
    {
        mockWorkers.Add(new WorkerState
        {
            FileName = $"large_file_{i:D4}.dat",
            Status = "Copying",
            Progress = 0.0,
            Speed = 0.0,
            BytesTransferred = 0,
            TotalBytes = (long)(100 * 1024 * 1024 * (1 + i * 0.3)) // Varying sizes
        });
    }

    // Set a demo speed limit
    TransferEngine.SetGlobalLimit(50L * 1024 * 1024); // 50 MB/s initial

    // Create interactive dashboard
    using var dashboard = new InteractiveDashboard(viewModel, pauseTokenSource);

    Console.WriteLine("Starting Interactive Dashboard Demo...");
    Console.WriteLine("Try the controls: P=Pause, H=Hide, +/-=Speed, U=Unlimited, R=Reset, Q=Quit");
    await Task.Delay(2000, cancellationToken);

    // Define the update function for mock data
    async Task UpdateMockDataAsync(CancellationToken ct)
    {
        var random = new Random();
        
        // Update mock workers
        foreach (var worker in mockWorkers)
        {
            // Only update if not paused
            if (worker.Status == "Copying" && !pauseTokenSource.IsPaused)
            {
                worker.BytesTransferred = Math.Min(
                    worker.BytesTransferred + random.Next(1, 10) * 1024 * 1024,
                    worker.TotalBytes);
                worker.Progress = (double)worker.BytesTransferred / worker.TotalBytes * 100.0;
                worker.Speed = random.Next(10, 150) * 1024 * 1024;

                if (worker.BytesTransferred >= worker.TotalBytes)
                {
                    worker.Status = "Completed";
                    worker.Speed = 0;
                }
            }
        }

        // Calculate global stats
        long totalTransferred = mockWorkers.Sum(w => w.BytesTransferred);
        long totalBytes = mockWorkers.Sum(w => w.TotalBytes);
        double globalProgress = totalBytes > 0 ? (double)totalTransferred / totalBytes : 0.0;
        double globalSpeed = mockWorkers.Where(w => w.Status == "Copying").Sum(w => w.Speed);
        int completedCount = mockWorkers.Count(w => w.Status == "Completed");
        int copyingCount = mockWorkers.Count(w => w.Status == "Copying");

        // Update ViewModel
        var stats = new TransferStats(
            workers: mockWorkers.ToArray(),
            globalSpeed: globalSpeed,
            progress: globalProgress,
            statusMessage: pauseTokenSource.IsPaused
                ? $"⏸ Paused (Demo) | Copying: {copyingCount} | Done: {completedCount}/{mockWorkers.Count}"
                : $"▶ Demo Mode | Copying: {copyingCount} | Done: {completedCount}/{mockWorkers.Count}",
            totalBytesTransferred: totalTransferred,
            totalBytes: totalBytes,
            completedCount: completedCount,
            failedCount: 0,
            isPaused: pauseTokenSource.IsPaused);

        viewModel.UpdateStats(stats);
    }

    // Run the interactive dashboard
    await dashboard.RunAsync(UpdateMockDataAsync, cancellationToken);

    Console.WriteLine("\n✓ Demo completed!");
}


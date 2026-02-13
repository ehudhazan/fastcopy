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
using System.CommandLine;
using System.Threading.Channels;

using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

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
    // Check if we're running in an AOT-compiled environment
    // In AOT builds, reflection is limited and Terminal.Gui TUI may not work properly
    bool isAotCompiled = !System.Runtime.CompilerServices.RuntimeFeature.IsDynamicCodeSupported;
    
    if (isAotCompiled)
    {
        // AOT builds require command-line arguments
        Console.WriteLine("FastCopy - High-performance file copy utility");
        Console.WriteLine();
        Console.WriteLine("No arguments provided. This AOT-compiled build requires command-line arguments.");
        Console.WriteLine();
        Console.WriteLine("Quick start:");
        Console.WriteLine("  fastcopy --src /source/path --dst /destination/path");
        Console.WriteLine("  fastcopy --file-list myfiles.txt --limit 100MB --parallel 8");
        Console.WriteLine();
        Console.WriteLine("Run 'fastcopy --help' for full list of options.");
        return;
    }
    
    try
    {
#pragma warning disable IL2026, IL3050
        // Initialize Terminal.Gui for interactive mode (non-AOT only)
        Application.Init();
        var menu = InteractiveMenu.Run();
        Application.Shutdown();
#pragma warning restore IL2026, IL3050
        
        if (menu == null)
        {
            Console.WriteLine("Operation cancelled by user.");
            return;
        }
        
        // Convert interactive menu settings to CLI arguments
        var argsList = new List<string>
        {
            "--src", menu.Source,
            "--dst", menu.Destination,
            "--retries", menu.Retries.ToString()
        };
        
        if (menu.Verify)
            argsList.Add("--verify");
        
        if (menu.DryRun)
            argsList.Add("--dry-run");
        
        if (menu.Delete)
            argsList.Add("--delete");
        
        if (menu.SpeedLimitBytesPerSec > 0)
        {
            argsList.Add("--limit");
            argsList.Add(menu.SpeedLimitBytesPerSec.ToString());
        }
        
        if (!string.IsNullOrWhiteSpace(menu.OnComplete))
        {
            argsList.Add("--on-complete");
            argsList.Add(menu.OnComplete);
        }
        
        args = argsList.ToArray();
    }
    catch (Exception ex)
    {
        // If TUI fails, show helpful error
        Console.WriteLine($"Error: Failed to launch interactive menu: {ex.Message}");
        Console.WriteLine("Please run with --help to see available options.");
        return;
    }
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


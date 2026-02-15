using System.Collections.Concurrent;
using System.Diagnostics;
using System.Threading.Channels;
using FastCopy.Processors;
using FastCopy.Services;
using FastCopy.UI;
using Spectre.Console;

namespace FastCopy.Core;

/// <summary>
/// Helper class for executing copy operations with various modes and options.
/// Preserves Zero-GC hot path and NativeAOT compatibility.
/// </summary>
public static class CopyOperationHelper
{
    /// <summary>
    /// Execute the main copy operation based on provided parameters.
    /// </summary>
    public static async Task ExecuteAsync(
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
        CancellationToken cancellationToken = default)
    {
        // Process priority
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

        // Parse rate limit
        long rateLimitBytesPerSec = 0;
        if (!string.IsNullOrEmpty(limitStr))
        {
            if (ByteSizeParser.TryParse(limitStr, out long parsedLimit) && parsedLimit >= 0)
            {
                rateLimitBytesPerSec = parsedLimit;
                TransferEngine.SetGlobalLimit(rateLimitBytesPerSec);
                Console.WriteLine($"Global rate limit set to {rateLimitBytesPerSec:N0} bytes/sec.");
            }
            else
            {
                Console.Error.WriteLine($"Error: --limit must be a valid size (e.g., 100MB, 1GB). Got: '{limitStr}'");
                Environment.Exit(1);
                return;
            }
        }

        // Legacy batch mode with --file-list
        if (!string.IsNullOrEmpty(fileListPath))
        {
            await ExecuteBatchModeAsync(fileListPath, parallel, retries, cancellationToken);
            return;
        }

        // Retry failed jobs mode
        if (!string.IsNullOrEmpty(retryFailed))
        {
            await ExecuteRetryFailedAsync(retryFailed, rateLimitBytesPerSec, maxMemMB ?? 0, parallel, retries, cancellationToken);
            
            // Execute on-complete command if provided
            if (!string.IsNullOrEmpty(onComplete))
            {
                await ExecuteOnCompleteCommand(onComplete);
            }
            return;
        }

        // Standard copy mode with --src and --dst
        if (!string.IsNullOrEmpty(src) && !string.IsNullOrEmpty(dst))
        {
            await ExecuteStandardCopyAsync(
                src, dst, verify, dryRun, delete, rateLimitBytesPerSec,
                maxMemMB ?? 0, parallel, retries, quiet, onComplete, cancellationToken);
            return;
        }

        // If no valid mode detected, show error
        Console.Error.WriteLine("Error: Either --src and --dst, --file-list, or --retry-failed must be provided.");
        Environment.Exit(1);
    }

    private static async Task ExecuteBatchModeAsync(
        string fileListPath,
        int parallel,
        int retries,
        CancellationToken cancellationToken)
    {
        Console.WriteLine($"Starting batch processing for: {fileListPath}");
        Console.WriteLine($"Parallel file transfers: {parallel}");

        var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        Console.CancelKeyPress += (s, e) =>
        {
            e.Cancel = true;
            Console.WriteLine("\nShutdown signal received. Stopping batch processing...");
            cts.Cancel();
        };

        try
        {
            var processor = new Processors.BatchProcessor();
            await processor.ProcessAsync(fileListPath, concurrency: parallel, cancellationToken: cts.Token);
            Console.WriteLine("Batch processing completed successfully.");
        }
        catch (OperationCanceledException)
        {
            Console.WriteLine("Batch processing was cancelled.");
            Environment.Exit(130);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Critical error: {ex.Message}");
            Environment.Exit(1);
        }
        finally
        {
            cts.Dispose();
        }
    }

    private static async Task ExecuteRetryFailedAsync(
        string failedJobsPath,
        long rateLimitBytesPerSec,
        long maxMemMB,
        int parallel,
        int retries,
        CancellationToken cancellationToken)
    {
        Console.WriteLine($"Retrying failed jobs from: {failedJobsPath}");
        Console.WriteLine($"Parallel file transfers: {parallel}");

        if (!File.Exists(failedJobsPath))
        {
            Console.Error.WriteLine($"Error: Failed jobs file not found: {failedJobsPath}");
            Environment.Exit(1);
            return;
        }

        // Setup infrastructure
        var transferEngine = new TransferEngine();
        var recoveryService = new RecoveryService();
        var deadLetterStore = new DeadLetterStore();
        var pauseTokenSource = new PauseTokenSource();
        
        TokenBucket? rateLimiter = rateLimitBytesPerSec > 0
            ? TransferEngine.GetGlobalRateLimiter()
            : null;

        var resourceWatchdog = new ResourceWatchdog(
            initialMaxThreads: parallel,
            maxMemoryMB: maxMemMB,
            statsCallback: null);

        var workerPool = new WorkerPool(
            transferEngine,
            deadLetterStore,
            recoveryService,
            rateLimiter,
            resourceWatchdog);

        // Create channel for jobs
        var channel = Channel.CreateUnbounded<CopyJob>();
        var activeTransfers = new ConcurrentDictionary<string, ActiveTransfer>();

        // Start workers with pause support
        var pauseToken = new PauseToken(pauseTokenSource);
        var workerTask = workerPool.StartConsumersAsync(
            channel.Reader,
            maxParallelism: parallel,
            activeTransfers,
            maxRetries: retries,
            stopOnError: false,
            pauseToken: pauseToken,
            cancellationToken: cancellationToken);

        // Stream jobs from JSONL file
        try
        {
            await foreach (var job in RecoveryService.ReadFailedJobsAsync(failedJobsPath, cancellationToken))
            {
                await channel.Writer.WriteAsync(job, cancellationToken);
                Console.WriteLine($"Queued retry: {job.Source} -> {job.Destination}");
            }
        }
        finally
        {
            channel.Writer.Complete();
        }

        // Launch interactive dashboard for retry operations
        await RunWithInteractiveDashboardAsync(workerTask, activeTransfers, pauseTokenSource, cancellationToken);

        // Cleanup
        await recoveryService.DisposeAsync();
        resourceWatchdog.Dispose();

        Console.WriteLine("Retry operation completed.");
    }

    private static async Task ExecuteStandardCopyAsync(
        string src,
        string dst,
        bool verify,
        bool dryRun,
        bool delete,
        long rateLimitBytesPerSec,
        long maxMemMB,
        int parallel,
        int retries,
        bool quiet,
        string? onComplete,
        CancellationToken cancellationToken)
    {
        Console.WriteLine($"Copy: {src} -> {dst}");
        Console.WriteLine($"Options: Verify={verify}, DryRun={dryRun}, Delete={delete}, Retries={retries}");
        Console.WriteLine($"Parallel file transfers: {parallel}");

        if (dryRun)
        {
            Console.WriteLine("[DRY RUN] No files will be copied.");
            return;
        }

        // Setup infrastructure
        var transferEngine = new TransferEngine();
        var recoveryService = new RecoveryService();
        var deadLetterStore = new DeadLetterStore();
        var pauseTokenSource = new PauseTokenSource();
        
        TokenBucket? rateLimiter = rateLimitBytesPerSec > 0
            ? TransferEngine.GetGlobalRateLimiter()
            : null;

        var resourceWatchdog = new ResourceWatchdog(
            initialMaxThreads: parallel,
            maxMemoryMB: maxMemMB,
            statsCallback: null);

        var workerPool = new WorkerPool(
            transferEngine,
            deadLetterStore,
            recoveryService,
            rateLimiter,
            resourceWatchdog);

        // Create channel for jobs
        var channel = Channel.CreateUnbounded<CopyJob>();
        var activeTransfers = new ConcurrentDictionary<string, ActiveTransfer>();

        // Start workers with pause token support
        var pauseToken = new PauseToken(pauseTokenSource);
        var workerTask = workerPool.StartConsumersAsync(
            channel.Reader,
            maxParallelism: parallel,
            activeTransfers,
            maxRetries: retries,
            stopOnError: false,
            pauseToken: pauseToken,
            cancellationToken: cancellationToken);

        // Queue the copy job(s)
        try
        {
            if (File.Exists(src))
            {
                // Single file copy
                var fileInfo = new FileInfo(src);
                var job = new CopyJob(src, dst, fileInfo.Length);
                await channel.Writer.WriteAsync(job, cancellationToken);
            }
            else if (Directory.Exists(src))
            {
                // Directory copy - recursively enumerate files
                Console.WriteLine("Scanning directory...");
                var files = Directory.EnumerateFiles(src, "*", SearchOption.AllDirectories);
                
                foreach (var file in files)
                {
                    var relativePath = Path.GetRelativePath(src, file);
                    var destPath = Path.Combine(dst, relativePath);
                    var fileInfo = new FileInfo(file);
                    
                    var job = new CopyJob(file, destPath, fileInfo.Length);
                    await channel.Writer.WriteAsync(job, cancellationToken);
                }
            }
            else
            {
                Console.Error.WriteLine($"Error: Source path not found: {src}");
                Environment.Exit(1);
                return;
            }
        }
        finally
        {
            channel.Writer.Complete();
        }

        // Launch Interactive Dashboard if not in quiet mode
        if (!quiet)
        {
            await RunWithInteractiveDashboardAsync(workerTask, activeTransfers, pauseTokenSource, cancellationToken);
        }
        else
        {
            // Quiet mode: just wait for workers to complete
            await workerTask;
        }

        // Cleanup
        await recoveryService.DisposeAsync();
        resourceWatchdog.Dispose();

        Console.WriteLine("Copy operation completed successfully.");

        // Execute on-complete command if provided
        if (!string.IsNullOrEmpty(onComplete))
        {
            await ExecuteOnCompleteCommand(onComplete);
        }

        // Delete source if requested
        if (delete)
        {
            Console.WriteLine("Deleting source files...");
            try
            {
                if (File.Exists(src))
                {
                    File.Delete(src);
                }
                else if (Directory.Exists(src))
                {
                    Directory.Delete(src, recursive: true);
                }
                Console.WriteLine("Source deleted successfully.");
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Error deleting source: {ex.Message}");
            }
        }
    }

    private static async Task ExecuteOnCompleteCommand(string command)
    {
        Console.WriteLine($"Executing on-complete command: {command}");

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = OperatingSystem.IsWindows() ? "cmd.exe" : "/bin/sh",
                Arguments = OperatingSystem.IsWindows() ? $"/c {command}" : $"-c \"{command}\"",
                UseShellExecute = true,
                CreateNoWindow = false
            };

            using var process = Process.Start(psi);
            
            if (process != null)
            {
                await process.WaitForExitAsync();
                Console.WriteLine($"On-complete command exited with code: {process.ExitCode}");
            }
            else
            {
                Console.Error.WriteLine("Failed to start on-complete command.");
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Error executing on-complete command: {ex.Message}");
        }
    }

    /// <summary>
    /// Run the interactive Spectre.Console TUI dashboard with keyboard controls.
    /// The dashboard allows real-time control: pause/resume, speed adjustment, visibility toggle.
    /// </summary>
    private static async Task RunWithInteractiveDashboardAsync(
        Task workerTask,
        ConcurrentDictionary<string, ActiveTransfer> activeTransfers,
        PauseTokenSource pauseTokenSource,
        CancellationToken cancellationToken)
    {
        // Create ViewModel and Interactive Dashboard
        var viewModel = new DashboardViewModel();
        using var dashboard = new InteractiveDashboard(viewModel, pauseTokenSource);

        // Define the stats update function
        async Task UpdateWorkerStatsAsync(CancellationToken ct)
        {
            // Build snapshot of current state
            var workers = new List<WorkerState>();
            long totalTransferred = 0;
            long totalBytes = 0;
            int completedCount = 0;
            int copyingCount = 0;
            int failedCount = 0;
            double totalSpeed = 0;

            foreach (var kvp in activeTransfers)
            {
                var transfer = kvp.Value;
                var workerState = new WorkerState
                {
                    FileName = Path.GetFileName(transfer.Source),
                    Status = transfer.Status,
                    Progress = transfer.TotalBytes > 0 
                        ? (double)transfer.BytesTransferred / transfer.TotalBytes * 100.0 
                        : 0.0,
                    Speed = transfer.BytesPerSecond,
                    BytesTransferred = transfer.BytesTransferred,
                    TotalBytes = transfer.TotalBytes
                };

                workers.Add(workerState);
                totalTransferred += transfer.BytesTransferred;
                totalBytes += transfer.TotalBytes;

                if (transfer.Status == "Completed")
                    completedCount++;
                else if (transfer.Status == "Copying")
                {
                    copyingCount++;
                    totalSpeed += transfer.BytesPerSecond;
                }
                else if (transfer.Status == "Failed")
                    failedCount++;
            }

            // Update ViewModel
            double globalProgress = totalBytes > 0 ? (double)totalTransferred / totalBytes : 0.0;
            var stats = new TransferStats(
                workers: workers.ToArray(),
                globalSpeed: totalSpeed,
                progress: globalProgress,
                statusMessage: pauseTokenSource.IsPaused 
                    ? $"⏸ Paused | Copying: {copyingCount} | Done: {completedCount} | Failed: {failedCount}"
                    : $"▶ Copying: {copyingCount} | Done: {completedCount} | Failed: {failedCount}",
                totalBytesTransferred: totalTransferred,
                totalBytes: totalBytes,
                completedCount: completedCount,
                failedCount: failedCount,
                isPaused: pauseTokenSource.IsPaused);

            viewModel.UpdateStats(stats);

            // Check if workers are done
            if (workerTask.IsCompleted)
            {
                // Allow dashboard to exit gracefully
                await Task.Delay(2000, ct);
                return;
            }
        }

        // Run the interactive dashboard
        await dashboard.RunAsync(UpdateWorkerStatsAsync, cancellationToken);

        // Wait for workers to complete if dashboard exited first
        if (!workerTask.IsCompleted)
        {
            Console.WriteLine("Waiting for transfers to complete...");
            try
            {
                await workerTask.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);
            }
            catch
            {
                Console.WriteLine("Warning: Some transfers may still be in progress.");
            }
        }
    }
}

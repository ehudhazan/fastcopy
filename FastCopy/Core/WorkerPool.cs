using System.Threading.Channels;
using System.Collections.Concurrent;
using System.Net.Sockets;
using FastCopy.Services;
using Renci.SshNet.Common;

namespace FastCopy.Core;

public sealed class WorkerPool
{
    private readonly TransferEngine _transferEngine;
    private readonly DeadLetterStore _deadLetterStore;
    private readonly RecoveryService _recoveryService;
    private readonly TokenBucket? _globalRateLimiter;
    private readonly ResourceWatchdog? _resourceWatchdog;
    private int _activeWorkerCount;

    /// <summary>
    /// Creates a new WorkerPool for managing parallel file transfers.
    /// </summary>
    /// <param name="transferEngine">Engine for performing file copies</param>
    /// <param name="deadLetterStore">Store for failed jobs</param>
    /// <param name="recoveryService">Service for logging and recovering failed jobs</param>
    /// <param name="globalRateLimiter">Optional global rate limiter shared across all workers. Pass null for unlimited speed.</param>
    /// <param name="resourceWatchdog">Optional resource watchdog for dynamic thread limiting based on memory pressure.</param>
    public WorkerPool(
        TransferEngine transferEngine, 
        DeadLetterStore deadLetterStore, 
        RecoveryService recoveryService,
        TokenBucket? globalRateLimiter = null,
        ResourceWatchdog? resourceWatchdog = null)
    {
        _transferEngine = transferEngine;
        _deadLetterStore = deadLetterStore;
        _recoveryService = recoveryService;
        _globalRateLimiter = globalRateLimiter;
        _resourceWatchdog = resourceWatchdog;
        _activeWorkerCount = 0;
    }

    public async Task StartConsumersAsync(
        ChannelReader<CopyJob> reader,
        int maxParallelism,
        ConcurrentDictionary<string, ActiveTransfer> activeTransfers,
        int maxRetries = 2,
        bool stopOnError = false,
        PauseToken pauseToken = default,
        CancellationToken cancellationToken = default)
    {
        // Use a SemaphoreSlim to strictly limit active Disk IO tasks.
        using var ioSemaphore = new SemaphoreSlim(maxParallelism);

        await Parallel.ForEachAsync(
            reader.ReadAllAsync(cancellationToken),
            new ParallelOptions
            {
                MaxDegreeOfParallelism = maxParallelism,
                CancellationToken = cancellationToken
            },
            async (job, ct) =>
            {
                // Respect dynamic thread limit from ResourceWatchdog
                if (_resourceWatchdog != null)
                {
                    while (Interlocked.CompareExchange(ref _activeWorkerCount, 0, 0) >= _resourceWatchdog.CurrentThreadLimit)
                    {
                        // Wait briefly if we're at or above the dynamic limit
                        await Task.Delay(50, ct);
                    }
                }

                Interlocked.Increment(ref _activeWorkerCount);
                
                await ioSemaphore.WaitAsync(ct);
                try
                {
                    // Create and add active transfer state
                    var transferState = new ActiveTransfer
                    {
                        Source = job.Source,
                        Destination = job.Destination,
                        TotalBytes = job.FileSize,
                        Status = "Copying",
                        BytesTransferred = 0
                    };

                    activeTransfers.TryAdd(job.Source, transferState);

                    // Retry loop with exponential backoff
                    Exception? lastException = null;
                    bool success = false;

                    for (int attempt = 0; attempt <= maxRetries && !success; attempt++)
                    {
                        try
                        {
                            await _transferEngine.CopyFileAsync(
                                job.Source,
                                job.Destination,
                                _globalRateLimiter, // Use global rate limiter shared across all workers
                                pauseToken,
                                (progress) =>
                                {
                                    // Update shared state
                                    transferState.BytesTransferred = progress.BytesTransferred;
                                    transferState.BytesPerSecond = progress.BytesPerSecond;
                                },
                                ct
                            );

                            success = true;
                            transferState.Status = "Completed";
                            transferState.BytesTransferred = job.FileSize;
                        }
                        catch (Exception ex) when (IsRetriableException(ex) && attempt < maxRetries)
                        {
                            lastException = ex;
                            
                            // Exponential backoff: 100ms * attempt
                            int delayMs = 100 * (attempt + 1);
                            await Task.Delay(delayMs, ct);
                            
                            // Reset progress for retry
                            transferState.BytesTransferred = 0;
                        }
                        catch (Exception ex)
                        {
                            // Non-retriable exception or final retry exhausted
                            lastException = ex;
                            break;
                        }
                    }

                    // Handle failure after all retries exhausted
                    if (!success && lastException != null)
                    {
                        if (stopOnError)
                        {
                            throw lastException;
                        }

                        // Log to RecoveryService for failed jobs after retry exhaustion
                        var failedJob = new FailedJob(job, lastException.Message);
                        await _recoveryService.LogFailure(failedJob);
                        
                        // Also add to DeadLetterStore for backward compatibility
                        _deadLetterStore.Add(job, lastException);
                        
                        if (activeTransfers.TryGetValue(job.Source, out var state))
                        {
                             state.Status = "Failed";
                        }
                    }
                }
                catch (Exception ex)
                {
                    if (stopOnError)
                    {
                        throw;
                    }

                    _deadLetterStore.Add(job, ex);
                    
                    if (activeTransfers.TryGetValue(job.Source, out var state))
                    {
                         state.Status = "Failed";
                    }
                }
                finally
                {
                    // Remove from active transfers so the TUI sees only currently moving files
                    activeTransfers.TryRemove(job.Source, out _);
                    
                    ioSemaphore.Release();
                    Interlocked.Decrement(ref _activeWorkerCount);
                }
            });
    }

    /// <summary>
    /// Determines if an exception is retriable based on its type.
    /// Only retries IOException, SocketException, or SshException.
    /// Does NOT retry UnauthorizedAccessException.
    /// </summary>
    private static bool IsRetriableException(Exception ex)
    {
        return ex is IOException 
            || ex is SocketException 
            || ex is SshException;
    }
}

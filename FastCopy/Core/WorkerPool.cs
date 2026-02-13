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

    public WorkerPool(TransferEngine transferEngine, DeadLetterStore deadLetterStore, RecoveryService recoveryService)
    {
        _transferEngine = transferEngine;
        _deadLetterStore = deadLetterStore;
        _recoveryService = recoveryService;
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
                                null, // No rate limit enforced here
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

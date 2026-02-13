using System.Threading.Channels;
using System.Collections.Concurrent;
using FastCopy.Services;

namespace FastCopy.Core;

public sealed class WorkerPool
{
    private readonly TransferEngine _transferEngine;
    private readonly DeadLetterStore _deadLetterStore;

    public WorkerPool(TransferEngine transferEngine, DeadLetterStore deadLetterStore)
    {
        _transferEngine = transferEngine;
        _deadLetterStore = deadLetterStore;
    }

    public async Task StartConsumersAsync(
        ChannelReader<CopyJob> reader,
        int maxParallelism,
        ConcurrentDictionary<string, ActiveTransfer> activeTransfers,
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

                    transferState.Status = "Completed";
                    transferState.BytesTransferred = job.FileSize;
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
}

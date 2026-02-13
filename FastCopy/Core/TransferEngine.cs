using System.Buffers;
using System.Diagnostics;
using System.IO.Pipelines;
using System.Runtime.InteropServices;

namespace FastCopy.Core;


/// <summary>
/// High-performance file transfer engine using System.IO.Pipelines for zero-copy operations.
/// Supports global rate limiting, pause/resume, and progress callbacks.
/// </summary>
public sealed class TransferEngine
{
    // Global rate limiter shared across all workers
    private static TokenBucket? _globalRateLimiter;
    private static readonly object _limiterLock = new();

    /// <summary>
    /// Set the global rate limit for all transfer operations.
    /// All workers will share the same TokenBucket and compete for bandwidth.
    /// </summary>
    /// <param name="bytesPerSec">Rate limit in bytes per second. 0 = unlimited.</param>
    public static void SetGlobalLimit(long bytesPerSec)
    {
        if (bytesPerSec < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(bytesPerSec), "Bytes per second must be non-negative.");
        }

        lock (_limiterLock)
        {
            if (_globalRateLimiter == null)
            {
                _globalRateLimiter = new TokenBucket(bytesPerSec);
            }
            else
            {
                _globalRateLimiter.SetLimit(bytesPerSec);
            }
        }
    }

    /// <summary>
    /// Get the current global rate limiter instance.
    /// Returns null if no global limit has been set.
    /// </summary>
    public static TokenBucket? GetGlobalRateLimiter()
    {
        lock (_limiterLock)
        {
            return _globalRateLimiter;
        }
    }

    /// <summary>
    /// Clear the global rate limit (removes all rate limiting).
    /// </summary>
    public static void ClearGlobalLimit()
    {
        lock (_limiterLock)
        {
            _globalRateLimiter = null;
        }
    }
    /// <summary>
    /// Copy a file asynchronously with optional rate limiting and progress tracking.
    /// </summary>
    /// <param name="sourcePath">Source file path</param>
    /// <param name="destinationPath">Destination file path</param>
    /// <param name="globalRateLimiter">Optional global rate limiter shared across all workers. Pass null to use the static global limiter (if set).</param>
    /// <param name="pauseToken">Token for pause/resume support</param>
    /// <param name="onProgress">Callback for progress updates</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <remarks>
    /// For global rate limiting across multiple concurrent copy operations, either:
    /// 1. Pass the same TokenBucket instance to all CopyFileAsync calls, OR
    /// 2. Call SetGlobalLimit() before starting transfers and pass null for globalRateLimiter.
    /// The TokenBucket is thread-safe and will aggregate throughput across all workers.
    /// </remarks>
    public async ValueTask CopyFileAsync(
        string sourcePath,
        string destinationPath,
        TokenBucket? globalRateLimiter = null,
        PauseToken pauseToken = default,
        ProgressCallback? onProgress = null,
        CancellationToken cancellationToken = default)
    {
        // Use the provided limiter, or fall back to the global static limiter
        TokenBucket? effectiveLimiter = globalRateLimiter ?? GetGlobalRateLimiter();

        var sourceOptions = new FileStreamOptions
        {
            Mode = FileMode.Open,
            Access = FileAccess.Read,
            Options = FileOptions.Asynchronous | FileOptions.SequentialScan,
            BufferSize = 0 // Disable FileStream buffer, rely on Pipe
        };

        var destOptions = new FileStreamOptions
        {
            Mode = FileMode.Create,
            Access = FileAccess.Write,
            Options = FileOptions.Asynchronous | (RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? FileOptions.WriteThrough : FileOptions.None),
            BufferSize = 0,
            PreallocationSize = new FileInfo(sourcePath).Length // Optimization
        };

        await using var sourceStream = new FileStream(sourcePath, sourceOptions);
        await using var destinationStream = new FileStream(destinationPath, destOptions);

        // Options
        var pipeReaderOptions = new StreamPipeReaderOptions(leaveOpen: true);
        var pipeWriterOptions = new StreamPipeWriterOptions(leaveOpen: true);

        var reader = PipeReader.Create(sourceStream, pipeReaderOptions);
        var writer = PipeWriter.Create(destinationStream, pipeWriterOptions);

        long totalBytes = sourceStream.Length;
        long copiedBytes = 0;
        long startTime = Stopwatch.GetTimestamp();

        try
        {
            while (true)
            {
                // Check pause before reading
                await pauseToken.WaitWhilePausedAsync(cancellationToken);

                ReadResult result = await reader.ReadAsync(cancellationToken);
                ReadOnlySequence<byte> buffer = result.Buffer;

                if (buffer.Length > 0)
                {
                    foreach (ReadOnlyMemory<byte> segment in buffer)
                    {
                        // Check pause before writing each segment
                        await pauseToken.WaitWhilePausedAsync(cancellationToken);

                        // Apply global rate limiting (Zero-GC, SpinWait-based)
                        effectiveLimiter?.Consume(segment.Length, cancellationToken);

                        await writer.WriteAsync(segment, cancellationToken);
                        copiedBytes += segment.Length;

                        if (onProgress != null)
                        {
                            double elapsedSeconds = (double)(Stopwatch.GetTimestamp() - startTime) / Stopwatch.Frequency;
                            double speed = elapsedSeconds > 0 ? copiedBytes / elapsedSeconds : 0;
                            onProgress(new TransferProgress(copiedBytes, totalBytes, speed));
                        }
                    }
                }

                reader.AdvanceTo(buffer.End);

                if (result.IsCompleted)
                {
                    break;
                }
            }
        }
        finally
        {
            await reader.CompleteAsync();
            await writer.CompleteAsync();
        }
    }
}


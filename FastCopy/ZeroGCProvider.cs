using System.Buffers;
using System.IO.Pipelines;

namespace FastCopy;

public sealed class ZeroGCProvider
{
    private readonly long _maxBytesPerSecond;

    public ZeroGCProvider(long maxBytesPerSecond = 0)
    {
        _maxBytesPerSecond = maxBytesPerSecond;
    }


    public async ValueTask CopyStreamAsync(Stream source, Stream destination, ZeroGCProgressCallback? onProgress = null, CancellationToken cancellationToken = default)
    {
        // Use PipeReader and PipeWriter to stream data efficiently with minimal buffering overhead.
        // We use MemoryPool<byte>.Shared which effectively pools memory (often using pinned arrays similar to ArrayPool).
        var reader = PipeReader.Create(source, new StreamPipeReaderOptions(pool: MemoryPool<byte>.Shared));
        var writer = PipeWriter.Create(destination, new StreamPipeWriterOptions(pool: MemoryPool<byte>.Shared));

        long totalBytes = 0;
        
        // Token bucket state
        long tokens = _maxBytesPerSecond; // Initial burst allowance
        long lastTick = Environment.TickCount64;

        try
        {
            while (true)
            {
                ReadResult result = await reader.ReadAsync(cancellationToken);
                ReadOnlySequence<byte> buffer = result.Buffer;

                if (buffer.Length > 0)
                {
                    // Throttle if limit is set
                    if (_maxBytesPerSecond > 0)
                    {
                        long bytesToProcess = buffer.Length;
                        
                        // Refill tokens based on elapsed time
                        long currentTick = Environment.TickCount64;
                        long elapsed = currentTick - lastTick;
                        
                        if (elapsed > 0)
                        {
                            long newTokens = (elapsed * _maxBytesPerSecond) / 1000;
                            tokens = Math.Min(_maxBytesPerSecond, tokens + newTokens);
                            lastTick = currentTick;
                        }

                        // Wait if not enough tokens
                        while (tokens < bytesToProcess)
                        {
                            long deficit = bytesToProcess - tokens;
                            int waitMs = (int)((deficit * 1000) / _maxBytesPerSecond);
                            if (waitMs < 1) waitMs = 1;

                            await Task.Delay(waitMs, cancellationToken);

                            // Refill after wait
                            currentTick = Environment.TickCount64;
                            elapsed = currentTick - lastTick;
                            if (elapsed > 0)
                            {
                                long newTokens = (elapsed * _maxBytesPerSecond) / 1000;
                                tokens = Math.Min(_maxBytesPerSecond, tokens + newTokens);
                                lastTick = currentTick;
                            }
                        }

                        tokens -= bytesToProcess;
                    }

                    // Copy from reader buffer to writer
                    foreach (var segment in buffer)
                    {
                        await writer.WriteAsync(segment, cancellationToken);
                    }
                    
                    totalBytes += buffer.Length;
                    
                    // Report progress
                    if (onProgress != null)
                    {
                        ReportProgress(totalBytes, onProgress);
                    }
                }

                reader.AdvanceTo(buffer.End);

                if (result.IsCompleted) break;
            }

            await writer.CompleteAsync();
            await reader.CompleteAsync();
        }
        catch (Exception ex)
        {
            await reader.CompleteAsync(ex);
            await writer.CompleteAsync(ex);
            throw;
        }
    }

    private void ReportProgress(long totalBytes, ZeroGCProgressCallback callback)
    {
        // Use ref struct for stack-allocated progress data
        var progress = new CopyProgress 
        { 
            TotalBytesCopied = totalBytes
        };
        
        callback(ref progress);
    }
}

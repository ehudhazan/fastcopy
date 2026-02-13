using System.Buffers;
using System.Diagnostics;
using System.IO.Pipelines;
using System.Runtime.InteropServices;

namespace FastCopy.Core;


public sealed class TransferEngine
{
    public async ValueTask CopyFileAsync(
        string sourcePath,
        string destinationPath,
        long? bytesPerSecondLimit = null,
        PauseToken pauseToken = default,
        ProgressCallback? onProgress = null,
        CancellationToken cancellationToken = default)
    {
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
        
        // Token bucket for rate limiting
        TokenBucket? rateLimiter = bytesPerSecondLimit.HasValue 
            ? new TokenBucket(bytesPerSecondLimit.Value) 
            : null;

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

                        if (rateLimiter != null)
                        {
                            await rateLimiter.ConsumeAsync(segment.Length, cancellationToken);
                        }

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


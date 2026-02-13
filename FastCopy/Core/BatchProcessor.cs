using System.Buffers;
using System.IO.Pipelines;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Channels;
using Microsoft.Win32.SafeHandles;

namespace FastCopy.Core;

// CopyJob is now defined in Models.cs

public static class BatchProcessor
{
    public static async Task ReadBatchFileAsync(string path, ChannelWriter<CopyJob> writer, CancellationToken cancellationToken = default)
    {
        if (!File.Exists(path))
        {
            throw new FileNotFoundException("File list not found", path);
        }

        using SafeFileHandle handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.Read, FileOptions.Asynchronous | FileOptions.SequentialScan);
        
        var pipe = new Pipe();
        Task writing = FillPipeAsync(handle, pipe.Writer, cancellationToken);
        Task reading = ReadPipeAsync(pipe.Reader, writer, cancellationToken);

        await Task.WhenAll(writing, reading);
    }

    private static async Task FillPipeAsync(SafeFileHandle handle, PipeWriter writer, CancellationToken cancellationToken)
    {
        const int bufferSize = 16384; // 16KB
        long fileOffset = 0;

        try 
        {
            while (true)
            {
                Memory<byte> memory = writer.GetMemory(bufferSize);
                int bytesRead = await RandomAccess.ReadAsync(handle, memory, fileOffset, cancellationToken);
                
                if (bytesRead == 0)
                {
                    break;
                }

                fileOffset += bytesRead;
                writer.Advance(bytesRead);

                FlushResult result = await writer.FlushAsync(cancellationToken);

                if (result.IsCompleted)
                {
                    break;
                }
            }
        }
        finally
        {
            await writer.CompleteAsync();
        }
    }

    private static async Task ReadPipeAsync(PipeReader reader, ChannelWriter<CopyJob> writer, CancellationToken cancellationToken)
    {
        try
        {
            while (true)
            {
                ReadResult result = await reader.ReadAsync(cancellationToken);
                ReadOnlySequence<byte> buffer = result.Buffer;

                while (TryReadLine(ref buffer, out ReadOnlySequence<byte> line))
                {
                    if (line.Length == 0) continue;

                    ReadOnlySpan<byte> lineSpan;
                    byte[]? rentedBuffer = null;

                    if (line.IsSingleSegment)
                    {
                        lineSpan = line.FirstSpan;
                    }
                    else
                    {
                        rentedBuffer = ArrayPool<byte>.Shared.Rent((int)line.Length);
                        line.CopyTo(rentedBuffer);
                        lineSpan = rentedBuffer.AsSpan(0, (int)line.Length);
                    }

                    try
                    {
                         int pipeIndex = lineSpan.IndexOf((byte)'|');
                         if (pipeIndex > 0)
                         {
                             ReadOnlySpan<byte> sourceBytes = lineSpan.Slice(0, pipeIndex);
                             ReadOnlySpan<byte> destBytes = lineSpan.Slice(pipeIndex + 1);

                             int maxCharCount = Encoding.UTF8.GetMaxCharCount(lineSpan.Length);
                             char[] charBuffer = ArrayPool<char>.Shared.Rent(maxCharCount);
                             
                             try 
                             {
                                 int sourceCharCount = Encoding.UTF8.GetChars(sourceBytes, charBuffer);
                                 ReadOnlySpan<char> sourceChars = charBuffer.AsSpan(0, sourceCharCount).Trim();
                                 string sourcePath = sourceChars.ToString();

                                 int destCharCount = Encoding.UTF8.GetChars(destBytes, charBuffer);
                                 ReadOnlySpan<char> destChars = charBuffer.AsSpan(0, destCharCount).Trim();
                                 string destPath = destChars.ToString();

                                 if (!string.IsNullOrEmpty(sourcePath) && !string.IsNullOrEmpty(destPath))
                                 {
                                     var job = new CopyJob(sourcePath, destPath, 0);
                                     
                                     if (!writer.TryWrite(job))
                                     {
                                         await writer.WriteAsync(job, cancellationToken);
                                     }
                                 }
                             }
                             finally
                             {
                                 ArrayPool<char>.Shared.Return(charBuffer);
                             }
                         }
                    }
                    finally
                    {
                        if (rentedBuffer != null)
                        {
                            ArrayPool<byte>.Shared.Return(rentedBuffer);
                        }
                    }
                }

                reader.AdvanceTo(buffer.Start, buffer.End);

                if (result.IsCompleted)
                {
                    break;
                }
            }
        }
        finally
        {
            writer.TryComplete();
        }
    }

    private static bool TryReadLine(ref ReadOnlySequence<byte> buffer, out ReadOnlySequence<byte> line)
    {
        SequencePosition? position = buffer.PositionOf((byte)'\n');

        if (position == null)
        {
            line = default;
            return false;
        }

        line = buffer.Slice(0, position.Value);
        buffer = buffer.Slice(buffer.GetPosition(1, position.Value));
        return true;
    }

    public static async Task StreamFileListAsync(string listFilePath, ChannelWriter<CopyJob> writer, CancellationToken cancellationToken = default)
    {
        if (!File.Exists(listFilePath))
        {
            throw new FileNotFoundException("File list not found", listFilePath);
        }

        // Use a small buffer size (4KB) to satisfy memory constraints
        const int BufferSize = 4096;
        
        using var stream = new FileStream(listFilePath, FileMode.Open, FileAccess.Read, FileShare.Read, BufferSize, FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var reader = new StreamReader(stream, bufferSize: BufferSize);

        string? line;
        while ((line = await reader.ReadLineAsync(cancellationToken)) != null)
        {
            // Zero-Allocation Parsing: Use Span to parse without creating intermediate strings
            ReadOnlySpan<char> lineSpan = line.AsSpan();
            
            // Skip empty lines
            if (lineSpan.IsWhiteSpace())
            {
                continue;
            }

            int separatorIndex = lineSpan.IndexOf('|');
            
            // Basic validation: skip lines without '|'
            if (separatorIndex == -1)
            {
                continue;
            }

            // Slice into Source and Destination
            ReadOnlySpan<char> sourceSpan = lineSpan.Slice(0, separatorIndex).Trim();
            ReadOnlySpan<char> destinationSpan = lineSpan.Slice(separatorIndex + 1).Trim();

            // Validate segments
            if (sourceSpan.IsEmpty || destinationSpan.IsEmpty)
            {
                continue;
            }

            // Only convert to string at the very last moment when creating the CopyJob struct
            // FileSize is unknown at this stage (scanning), so we set it to 0.
            // A separate step in the pipeline or the consumer should handle size resolution if needed.
            var job = new CopyJob(sourceSpan.ToString(), destinationSpan.ToString(), 0);

            // Backpressure: WriteAsync waits if the channel is full
            await writer.WriteAsync(job, cancellationToken);
        }
    }
}

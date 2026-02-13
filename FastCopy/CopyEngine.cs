using System.IO.Pipelines;

namespace FastCopy;

public sealed class CopyEngine
{
    public async Task CopyAsync(Stream source, Stream destination, CancellationToken cancellationToken = default)
    {
        var reader = PipeReader.Create(source);
        var writer = PipeWriter.Create(destination);

        try
        {
            while (true)
            {
                ReadResult result = await reader.ReadAsync(cancellationToken);
                var buffer = result.Buffer;

                try
                {
                    if (result.IsCanceled)
                    {
                        break;
                    }

                    if (!buffer.IsEmpty)
                    {
                        foreach (var segment in buffer)
                        {
                            await writer.WriteAsync(segment, cancellationToken);
                        }
                    }

                    if (result.IsCompleted)
                    {
                        break;
                    }
                }
                finally
                {
                    reader.AdvanceTo(buffer.End);
                }

                await writer.FlushAsync(cancellationToken);
            }
        }
        finally
        {
            await reader.CompleteAsync();
            await writer.CompleteAsync();
        }
    }
}

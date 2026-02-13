using System.Threading.Channels;
using FastCopy.Transports;

namespace FastCopy.Processors;

public sealed class BatchProcessor
{
    private readonly CopyEngine _engine;

    public BatchProcessor()
    {
        _engine = new CopyEngine();
    }

    public async Task ProcessAsync(string fileListPath, int concurrency = 4, CancellationToken cancellationToken = default)
    {
        if (!File.Exists(fileListPath))
        {
            throw new FileNotFoundException("File list not found", fileListPath);
        }

        var channel = Channel.CreateBounded<string>(new BoundedChannelOptions(1000)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = false,
            SingleWriter = true
        });

        // Producer task
        var producer = Task.Run(async () =>
        {
            try
            {
                using var reader = File.OpenText(fileListPath);
                string? line;
                while ((line = await reader.ReadLineAsync(cancellationToken)) != null)
                {
                    // Skip empty lines and comments
                    if (string.IsNullOrWhiteSpace(line) || line.TrimStart().StartsWith('#'))
                        continue;
                    
                    await channel.Writer.WriteAsync(line, cancellationToken);
                }
                channel.Writer.Complete();
            }
            catch (Exception ex)
            {
                channel.Writer.Complete(ex);
            }
        }, cancellationToken);

        // Consumer (Worker Pool)
        try
        {
            await Parallel.ForEachAsync(channel.Reader.ReadAllAsync(cancellationToken),
                new ParallelOptions
                {
                    MaxDegreeOfParallelism = concurrency,
                    CancellationToken = cancellationToken
                },
                async (line, ct) =>
                {
                    var parts = line.Split('|');
                    if (parts.Length != 2)
                    {
                        Console.Error.WriteLine($"Invalid line format: {line}");
                        return;
                    }

                    var source = parts[0].Trim();
                    var destination = parts[1].Trim();

                    try
                    {
                        // TODO: Determine if source/destination is remote and use ISshTransport
                        // For now, defaulting to local file system as per current scope.
                        // The ISshTransport interface is available for future extension or dependency injection.
                        
                        using var sourceStream = File.OpenRead(source);
                        
                        // Ensure directory exists
                        var destDir = Path.GetDirectoryName(destination);
                        if (!string.IsNullOrEmpty(destDir) && !Directory.Exists(destDir))
                        {
                            Directory.CreateDirectory(destDir);
                        }

                        using var destStream = File.Create(destination);

                        await _engine.CopyAsync(sourceStream, destStream, ct);
                        
                        // Simple console output for progress (could be replaced with TUI later)
                        Console.WriteLine($"Copied: {source} -> {destination}");
                    }
                    catch (Exception ex)
                    {
                        Console.Error.WriteLine($"Failed to copy {source} to {destination}: {ex.Message}");
                    }
                });
        }
        catch (OperationCanceledException)
        {
            // Graceful shutdown
        }
        finally
        {
            await producer; // Propagate any producer exceptions
        }
    }
}

using System.Buffers;
using System.Text.Json;

namespace FastCopy.Core;

/// <summary>
/// High-performance, thread-safe service for logging failed copy jobs to JSONL format.
/// Uses periodic flushing to minimize IOPS overhead.
/// </summary>
public sealed class RecoveryService : IAsyncDisposable
{
    private readonly string _logFilePath;
    private readonly FileStream _fileStream;
    private readonly SemaphoreSlim _writeLock;
    private readonly Timer _flushTimer;
    private readonly int _flushIntervalMs;
    private int _pendingWrites;
    private bool _disposed;

    private const int DefaultFlushIntervalMs = 5000; // Flush every 5 seconds
    private const int BufferSize = 64 * 1024; // 64KB buffer

    /// <summary>
    /// Initializes a new instance of the RecoveryService.
    /// </summary>
    /// <param name="flushIntervalMs">Interval in milliseconds between automatic flushes.</param>
    public RecoveryService(int flushIntervalMs = DefaultFlushIntervalMs)
    {
        _flushIntervalMs = flushIntervalMs;
        _writeLock = new SemaphoreSlim(1, 1);
        
        var timestamp = DateTime.UtcNow.ToString("yyyyMMdd_HHmmss");
        _logFilePath = $"failed_jobs_{timestamp}.jsonl";
        
        _fileStream = new FileStream(
            _logFilePath,
            FileMode.Append,
            FileAccess.Write,
            FileShare.Read,
            BufferSize,
            useAsync: true);

        _flushTimer = new Timer(FlushTimerCallback, null, _flushIntervalMs, _flushIntervalMs);
    }

    /// <summary>
    /// Logs a failed job to the JSONL file in a thread-safe manner.
    /// Uses Utf8JsonWriter for zero-allocation serialization.
    /// </summary>
    /// <param name="failure">The failed job to log.</param>
    public async Task LogFailure(FailedJob failure)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        await _writeLock.WaitAsync();
        try
        {
            var bufferWriter = new ArrayBufferWriter<byte>(1024);
            
            using (var writer = new Utf8JsonWriter(bufferWriter, new JsonWriterOptions 
            { 
                Indented = false,
                SkipValidation = true // Slight performance gain, we control the structure
            }))
            {
                writer.WriteStartObject();
                
                writer.WriteString("timestamp", DateTime.UtcNow);
                
                writer.WritePropertyName("job");
                writer.WriteStartObject();
                writer.WriteString("source", failure.Job.Source);
                writer.WriteString("destination", failure.Job.Destination);
                writer.WriteNumber("fileSize", failure.Job.FileSize);
                writer.WriteEndObject();
                
                writer.WriteString("exceptionMessage", failure.ExceptionMessage);
                
                writer.WriteEndObject();
            }

            var jsonBytes = bufferWriter.WrittenMemory;
            await _fileStream.WriteAsync(jsonBytes);
            
            // Write newline
            await _fileStream.WriteAsync(new byte[] { (byte)'\n' });
            
            Interlocked.Increment(ref _pendingWrites);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <summary>
    /// Manually flushes pending writes to disk.
    /// </summary>
    public async Task FlushAsync()
    {
        if (_disposed) return;

        await _writeLock.WaitAsync();
        try
        {
            if (_pendingWrites > 0)
            {
                await _fileStream.FlushAsync();
                Interlocked.Exchange(ref _pendingWrites, 0);
            }
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private void FlushTimerCallback(object? state)
    {
        if (_disposed || _pendingWrites == 0) return;

        // Fire and forget - we don't want to block the timer thread
        _ = Task.Run(async () =>
        {
            try
            {
                await FlushAsync();
            }
            catch
            {
                // Suppress exceptions in timer callback to prevent crashes
            }
        });
    }

    /// <summary>
    /// Reads failed jobs from a JSONL file for retry operations.
    /// </summary>
    /// <param name="filePath">Path to the JSONL file containing failed jobs.</param>
    /// <param name="cancellationToken">Cancellation token for stopping the read operation.</param>
    /// <returns>An async enumerable of CopyJob instances.</returns>
    public static async IAsyncEnumerable<CopyJob> ReadFailedJobsAsync(
        string filePath,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (!File.Exists(filePath))
        {
            throw new FileNotFoundException($"Failed jobs file not found: {filePath}");
        }

        await using var fileStream = new FileStream(
            filePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 64 * 1024,
            useAsync: true);

        using var reader = new StreamReader(fileStream);
        string? line;
        
        while ((line = await reader.ReadLineAsync(cancellationToken)) != null)
        {
            if (string.IsNullOrWhiteSpace(line))
                continue;

            // Parse the JSONL line
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;
            
            if (root.TryGetProperty("job", out var jobElement))
            {
                var source = jobElement.GetProperty("source").GetString();
                var destination = jobElement.GetProperty("destination").GetString();
                var fileSize = jobElement.GetProperty("fileSize").GetInt64();
                
                if (!string.IsNullOrEmpty(source) && !string.IsNullOrEmpty(destination))
                {
                    yield return new CopyJob(source, destination, fileSize);
                }
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        await _flushTimer.DisposeAsync();
        
        // Final flush before disposal
        await FlushAsync();
        
        _writeLock.Dispose();
        await _fileStream.DisposeAsync();
    }
}

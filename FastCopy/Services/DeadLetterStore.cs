using System.Text.Json;
using System.Text.Json.Serialization;
using FastCopy.Core;

namespace FastCopy.Services;

public sealed partial class DeadLetterStore
{
    private readonly string _logPath;
    private readonly Lock _lock = new();

    public DeadLetterStore(string? logPath = null)
    {
        _logPath = logPath ?? "dead_letters.json";
    }

    public void Add(CopyJob job, Exception ex)
    {
        var entry = new FailedJobEntry
        {
            Timestamp = DateTime.UtcNow,
            Source = job.Source,
            Destination = job.Destination,
            FileSize = job.FileSize,
            ErrorMessage = ex.Message,
            StackTrace = ex.StackTrace
        };

        string json = JsonSerializer.Serialize(entry, DeadLetterStoreContext.Default.FailedJobEntry);

        lock (_lock)
        {
            File.AppendAllLines(_logPath, [json]);
        }
    }


}



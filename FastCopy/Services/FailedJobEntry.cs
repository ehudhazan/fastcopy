using System;

namespace FastCopy.Services;

internal sealed class FailedJobEntry
{
    public DateTime Timestamp { get; set; }
    public string Source { get; set; } = string.Empty;
    public string Destination { get; set; } = string.Empty;
    public long FileSize { get; set; }
    public string ErrorMessage { get; set; } = string.Empty;
    public string? StackTrace { get; set; }
}

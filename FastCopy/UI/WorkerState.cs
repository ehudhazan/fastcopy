namespace FastCopy.UI;

/// <summary>
/// Represents the state of a single worker/transfer for TUI display.
/// Designed to be observable and efficiently updated by the ViewModel.
/// </summary>
public sealed class WorkerState
{
    public string FileName { get; set; } = string.Empty;
    public string Status { get; set; } = "Pending";
    public double Progress { get; set; }
    public double Speed { get; set; }
    public long BytesTransferred { get; set; }
    public long TotalBytes { get; set; }
    
    /// <summary>
    /// Helper property for formatted speed (e.g., "120 MB/s")
    /// </summary>
    public string FormattedSpeed => FormatSpeed(Speed);
    
    /// <summary>
    /// Helper property for formatted progress (e.g., "45%")
    /// </summary>
    public string FormattedProgress => $"{Progress:F1}%";

    private static string FormatSpeed(double bytesPerSec)
    {
        if (bytesPerSec < 1024)
            return $"{bytesPerSec:F0} B/s";
        if (bytesPerSec < 1024 * 1024)
            return $"{bytesPerSec / 1024:F1} KB/s";
        if (bytesPerSec < 1024 * 1024 * 1024)
            return $"{bytesPerSec / (1024 * 1024):F1} MB/s";
        return $"{bytesPerSec / (1024 * 1024 * 1024):F2} GB/s";
    }
}

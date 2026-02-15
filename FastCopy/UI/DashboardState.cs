namespace FastCopy.UI;

/// <summary>
/// Immutable state structure for rendering the Dashboard.
/// Designed for zero-allocation passing to render methods.
/// </summary>
public readonly struct DashboardState
{
    public readonly ReadOnlyMemory<TransferItem> Transfers;
    public readonly double GlobalProgress;
    public readonly long TotalBytesTransferred;
    public readonly long TotalBytes;
    public readonly double AverageSpeed;
    public readonly int CompletedCount;
    public readonly int CopyingCount;
    public readonly int PausedCount;
    public readonly int FailedCount;
    public readonly bool IsPaused;
    public readonly string Status;
    public readonly ResourceStats Resources;

    public DashboardState(
        ReadOnlyMemory<TransferItem> transfers,
        double globalProgress,
        long totalBytesTransferred,
        long totalBytes,
        double averageSpeed,
        int completedCount,
        int copyingCount,
        int pausedCount,
        int failedCount,
        bool isPaused,
        string status,
        ResourceStats resources)
    {
        Transfers = transfers;
        GlobalProgress = globalProgress;
        TotalBytesTransferred = totalBytesTransferred;
        TotalBytes = totalBytes;
        AverageSpeed = averageSpeed;
        CompletedCount = completedCount;
        CopyingCount = copyingCount;
        PausedCount = pausedCount;
        FailedCount = failedCount;
        IsPaused = isPaused;
        Status = status;
        Resources = resources;
    }
}

/// <summary>
/// Resource monitoring statistics.
/// </summary>
public readonly struct ResourceStats
{
    public readonly double MemoryUsageMB;
    public readonly double MaxMemoryMB;
    public readonly double CpuUsagePercent;
    public readonly int CurrentThreads;
    public readonly int MaxThreads;
    public readonly bool IsThrottled;

    public ResourceStats(
        double memoryUsageMB,
        double maxMemoryMB,
        double cpuUsagePercent,
        int currentThreads,
        int maxThreads,
        bool isThrottled)
    {
        MemoryUsageMB = memoryUsageMB;
        MaxMemoryMB = maxMemoryMB;
        CpuUsagePercent = cpuUsagePercent;
        CurrentThreads = currentThreads;
        MaxThreads = maxThreads;
        IsThrottled = isThrottled;
    }

    public static ResourceStats Empty => new(0, 0, 0, 0, 0, false);
}

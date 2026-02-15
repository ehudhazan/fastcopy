using System.Collections.Concurrent;
using FastCopy.Core;

namespace FastCopy.UI;

/// <summary>
/// NativeAOT-safe ViewModel for the FastCopy dashboard using Spectre.Console.
/// Thread-safe property updates for real-time display.
/// </summary>
public sealed class DashboardViewModel
{
    private readonly object _lock = new();
    private string _globalSpeed = "0 B/s";
    private double _progress = 0.0;
    private string _statusMessage = "Initializing...";
    private readonly List<WorkerState> _workers = new();
    private long _totalBytesTransferred = 0;
    private long _totalBytes = 0;
    private int _completedCount = 0;
    private int _failedCount = 0;
    private bool _isPaused = false;
    private string? _notification = null;
    private DateTime _notificationTime = DateTime.MinValue;
    private int _maxWorkers = 0;

    public string GlobalSpeed { get { lock (_lock) return _globalSpeed; } }
    public double Progress { get { lock (_lock) return _progress; } }
    public string StatusMessage { get { lock (_lock) return _statusMessage; } }
    public long TotalBytesTransferred { get { lock (_lock) return _totalBytesTransferred; } }
    public long TotalBytes { get { lock (_lock) return _totalBytes; } }
    public int CompletedCount { get { lock (_lock) return _completedCount; } }
    public int FailedCount { get { lock (_lock) return _failedCount; } }
    public bool IsPaused { get { lock (_lock) return _isPaused; } }
    public int MaxWorkers { get { lock (_lock) return _maxWorkers; } }
    
    /// <summary>
    /// Get the current notification if it's still active (displayed for 3 seconds).
    /// </summary>
    public string? GetActiveNotification()
    {
        lock (_lock)
        {
            if (_notification != null && (DateTime.UtcNow - _notificationTime).TotalSeconds < 3)
            {
                return _notification;
            }
            return null;
        }
    }
    
    /// <summary>
    /// Set a notification message to be displayed for 3 seconds.
    /// Thread-safe, can be called from any thread.
    /// </summary>
    public void SetNotification(string message)
    {
        lock (_lock)
        {
            _notification = message;
            _notificationTime = DateTime.UtcNow;
        }
    }
    
    /// <summary>
    /// Set the max workers count.
    /// Thread-safe, can be called from any thread.
    /// </summary>
    public void SetMaxWorkers(int count)
    {
        lock (_lock)
        {
            _maxWorkers = count;
        }
    }

    /// <summary>
    /// Get a snapshot of current workers (thread-safe copy).
    /// </summary>
    public List<WorkerState> GetWorkerSnapshot()
    {
        lock (_lock)
        {
            return new List<WorkerState>(_workers);
        }
    }

    /// <summary>
    /// Update the ViewModel with new transfer statistics.
    /// This method is designed to be called from the WorkerPool with minimal allocations.
    /// </summary>
    public void UpdateStats(TransferStats stats)
    {
        lock (_lock)
        {
            _globalSpeed = FormatSpeed(stats.GlobalSpeed);
            _progress = stats.Progress;
            _statusMessage = stats.StatusMessage;
            _totalBytesTransferred = stats.TotalBytesTransferred;
            _totalBytes = stats.TotalBytes;
            _completedCount = stats.CompletedCount;
            _failedCount = stats.FailedCount;
            _isPaused = stats.IsPaused;

            // Update worker collection
            UpdateWorkers(stats.Workers.Span);
        }
    }

    /// <summary>
    /// Update the workers collection with minimal allocations.
    /// </summary>
    private void UpdateWorkers(ReadOnlySpan<WorkerState> newWorkers)
    {
        // Clear if size changed significantly
        if (_workers.Count > newWorkers.Length * 2)
        {
            _workers.Clear();
        }

        // Update or add workers
        for (int i = 0; i < newWorkers.Length; i++)
        {
            var newWorker = newWorkers[i];
            
            if (i < _workers.Count)
            {
                // Update existing
                var existing = _workers[i];
                existing.FileName = newWorker.FileName;
                existing.Status = newWorker.Status;
                existing.Progress = newWorker.Progress;
                existing.Speed = newWorker.Speed;
                existing.BytesTransferred = newWorker.BytesTransferred;
                existing.TotalBytes = newWorker.TotalBytes;
            }
            else
            {
                // Add new
                _workers.Add(new WorkerState
                {
                    FileName = newWorker.FileName,
                    Status = newWorker.Status,
                    Progress = newWorker.Progress,
                    Speed = newWorker.Speed,
                    BytesTransferred = newWorker.BytesTransferred,
                    TotalBytes = newWorker.TotalBytes
                });
            }
        }

        // Remove excess
        while (_workers.Count > newWorkers.Length)
        {
            _workers.RemoveAt(_workers.Count - 1);
        }
    }

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

/// <summary>
/// Transfer statistics snapshot for updating the ViewModel.
/// Designed to be passed from WorkerPool to ViewModel with zero allocations.
/// </summary>
public readonly struct TransferStats
{
    public readonly ReadOnlyMemory<WorkerState> Workers;
    public readonly double GlobalSpeed;
    public readonly double Progress;
    public readonly string StatusMessage;
    public readonly long TotalBytesTransferred;
    public readonly long TotalBytes;
    public readonly int CompletedCount;
    public readonly int FailedCount;
    public readonly bool IsPaused;

    public TransferStats(
        ReadOnlyMemory<WorkerState> workers,
        double globalSpeed,
        double progress,
        string statusMessage,
        long totalBytesTransferred,
        long totalBytes,
        int completedCount,
        int failedCount,
        bool isPaused)
    {
        Workers = workers;
        GlobalSpeed = globalSpeed;
        Progress = progress;
        StatusMessage = statusMessage;
        TotalBytesTransferred = totalBytesTransferred;
        TotalBytes = totalBytes;
        CompletedCount = completedCount;
        FailedCount = failedCount;
        IsPaused = isPaused;
    }
}

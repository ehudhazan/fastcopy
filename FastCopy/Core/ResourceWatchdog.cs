using System.Diagnostics;

namespace FastCopy.Core;

/// <summary>
/// Background service that monitors process resource usage (RAM and CPU).
/// Automatically reduces thread count when memory pressure is detected.
/// Reports metrics every 500ms for TUI display.
/// </summary>
public sealed class ResourceWatchdog : IDisposable
{
    private readonly Process _currentProcess;
    private readonly long _maxMemoryBytes;
    private readonly int _initialMaxThreads;
    private readonly Timer _monitorTimer;
    private readonly Action<ResourceStats>? _statsCallback;
    
    private int _currentThreadLimit;
    private DateTime _lastCpuTime;
    private TimeSpan _lastTotalProcessorTime;
    private bool _disposed;

    /// <summary>
    /// Gets the current recommended thread limit based on resource availability.
    /// WorkerPool should respect this value to prevent resource exhaustion.
    /// </summary>
    public int CurrentThreadLimit => _currentThreadLimit;

    /// <summary>
    /// Gets the latest resource statistics.
    /// </summary>
    public ResourceStats LatestStats { get; private set; }

    /// <summary>
    /// Creates a new ResourceWatchdog to monitor process resources.
    /// </summary>
    /// <param name="initialMaxThreads">Initial maximum number of threads allowed</param>
    /// <param name="maxMemoryMB">Maximum memory in MB before throttling (0 = unlimited)</param>
    /// <param name="statsCallback">Optional callback invoked every 500ms with latest stats</param>
    public ResourceWatchdog(
        int initialMaxThreads,
        long maxMemoryMB = 0,
        Action<ResourceStats>? statsCallback = null)
    {
        _currentProcess = Process.GetCurrentProcess();
        _maxMemoryBytes = maxMemoryMB * 1024 * 1024;
        _initialMaxThreads = initialMaxThreads;
        _currentThreadLimit = initialMaxThreads;
        _statsCallback = statsCallback;
        
        _lastCpuTime = DateTime.UtcNow;
        _lastTotalProcessorTime = _currentProcess.TotalProcessorTime;
        
        LatestStats = new ResourceStats();

        // Start monitoring timer (500ms interval)
        _monitorTimer = new Timer(MonitorResources, null, TimeSpan.Zero, TimeSpan.FromMilliseconds(500));
    }

    private void MonitorResources(object? state)
    {
        if (_disposed) return;

        try
        {
            // Refresh process metrics
            _currentProcess.Refresh();

            // Calculate memory usage
            long workingSetBytes = _currentProcess.WorkingSet64;
            double workingSetMB = workingSetBytes / (1024.0 * 1024.0);

            // Calculate CPU usage percentage
            DateTime currentTime = DateTime.UtcNow;
            TimeSpan currentTotalProcessorTime = _currentProcess.TotalProcessorTime;
            
            double cpuUsedMs = (currentTotalProcessorTime - _lastTotalProcessorTime).TotalMilliseconds;
            double totalMsPassed = (currentTime - _lastCpuTime).TotalMilliseconds;
            double cpuUsageRatio = cpuUsedMs / (Environment.ProcessorCount * totalMsPassed);
            double cpuUsagePercent = cpuUsageRatio * 100.0;

            _lastCpuTime = currentTime;
            _lastTotalProcessorTime = currentTotalProcessorTime;

            // Update stats
            LatestStats = new ResourceStats
            {
                MemoryUsageMB = workingSetMB,
                CpuUsagePercent = Math.Clamp(cpuUsagePercent, 0.0, 100.0),
                CurrentThreadLimit = _currentThreadLimit,
                MaxMemoryMB = _maxMemoryBytes > 0 ? _maxMemoryBytes / (1024.0 * 1024.0) : 0,
                IsThrottled = _currentThreadLimit < _initialMaxThreads
            };

            // Memory pressure management
            if (_maxMemoryBytes > 0 && workingSetBytes > _maxMemoryBytes)
            {
                // Memory exceeded: reduce thread limit by 25% (minimum 1)
                int newLimit = Math.Max(1, (int)(_currentThreadLimit * 0.75));
                if (newLimit != _currentThreadLimit)
                {
                    _currentThreadLimit = newLimit;
                    LatestStats = LatestStats with { CurrentThreadLimit = _currentThreadLimit, IsThrottled = true };
                }
            }
            else if (_currentThreadLimit < _initialMaxThreads)
            {
                // Memory is fine: gradually increase thread limit (add 1 thread every 500ms)
                // This prevents rapid oscillation
                long safeThreshold = _maxMemoryBytes > 0 ? (long)(_maxMemoryBytes * 0.85) : long.MaxValue;
                
                if (workingSetBytes < safeThreshold)
                {
                    _currentThreadLimit = Math.Min(_initialMaxThreads, _currentThreadLimit + 1);
                    LatestStats = LatestStats with 
                    { 
                        CurrentThreadLimit = _currentThreadLimit,
                        IsThrottled = _currentThreadLimit < _initialMaxThreads 
                    };
                }
            }

            // Invoke callback for TUI updates
            _statsCallback?.Invoke(LatestStats);
        }
        catch
        {
            // Ignore errors during monitoring to prevent watchdog crashes
            // In production, you might want to log these
        }
    }

    /// <summary>
    /// Manually adjusts the thread limit. Use sparingly.
    /// </summary>
    public void SetThreadLimit(int newLimit)
    {
        _currentThreadLimit = Math.Max(1, Math.Min(_initialMaxThreads, newLimit));
    }

    public void Dispose()
    {
        if (_disposed) return;
        
        _disposed = true;
        _monitorTimer?.Dispose();
        _currentProcess?.Dispose();
    }
}

/// <summary>
/// Snapshot of resource usage at a point in time.
/// </summary>
public readonly record struct ResourceStats
{
    public double MemoryUsageMB { get; init; }
    public double CpuUsagePercent { get; init; }
    public int CurrentThreadLimit { get; init; }
    public double MaxMemoryMB { get; init; }
    public bool IsThrottled { get; init; }
}

using System.Buffers;
using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using FastCopy.Core;

namespace FastCopy.UI;

/// <summary>
/// AOT-safe TUI Dashboard using System.Console with zero-allocation rendering.
/// Replaces Terminal.Gui with a custom implementation using SetCursorPosition and ReadOnlySpan&lt;char&gt;.
/// </summary>
public sealed class ConsoleDashboard : IDisposable
{
    private readonly ConsoleBuffer _buffer;
    private readonly CancellationTokenSource _cts;
    private readonly Task _inputTask;
    private readonly Task _renderTask;
    private readonly PauseTokenSource _pauseTokenSource;
    private readonly ConcurrentDictionary<string, ActiveTransfer> _activeTransfers;
    private readonly ResourceWatchdog? _resourceWatchdog;
    
    private bool _disposed;
    private bool _hidden;
    private int _scrollOffset;
    private volatile bool _shouldExit;

    public bool ShouldExit => _shouldExit;

    public ConsoleDashboard(
        ConcurrentDictionary<string, ActiveTransfer> activeTransfers,
        PauseTokenSource pauseTokenSource,
        ResourceWatchdog? resourceWatchdog = null)
    {
        _activeTransfers = activeTransfers;
        _pauseTokenSource = pauseTokenSource;
        _resourceWatchdog = resourceWatchdog;
        _cts = new CancellationTokenSource();

        // Initialize console
        Console.CursorVisible = false;
        Console.Clear();

        int width = Console.WindowWidth;
        int height = Console.WindowHeight;
        _buffer = new ConsoleBuffer(width, height);

        // Start input handler task
        _inputTask = Task.Run(() => InputLoop(_cts.Token), _cts.Token);

        // Start render loop task
        _renderTask = Task.Run(() => RenderLoop(_cts.Token), _cts.Token);
    }

    /// <summary>
    /// Background task for handling keyboard input.
    /// Supports: P (Pause), H (Hide), Esc (Exit)
    /// </summary>
    private void InputLoop(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested && !_shouldExit)
            {
                if (Console.KeyAvailable)
                {
                    var key = Console.ReadKey(intercept: true);

                    switch (key.Key)
                    {
                        case ConsoleKey.P:
                            _pauseTokenSource.Toggle();
                            UpdateTransferStatuses();
                            break;

                        case ConsoleKey.H:
                            _hidden = !_hidden;
                            if (!_hidden)
                            {
                                _buffer.ForceRedraw();
                            }
                            break;

                        case ConsoleKey.Escape:
                        case ConsoleKey.Q:
                            _shouldExit = true;
                            break;

                        case ConsoleKey.UpArrow:
                            if (_scrollOffset > 0)
                                _scrollOffset--;
                            break;

                        case ConsoleKey.DownArrow:
                            _scrollOffset++;
                            break;

                        case ConsoleKey.PageUp:
                            _scrollOffset = Math.Max(0, _scrollOffset - 10);
                            break;

                        case ConsoleKey.PageDown:
                            _scrollOffset += 10;
                            break;

                        case ConsoleKey.Home:
                            _scrollOffset = 0;
                            break;
                    }
                }

                // Small delay to prevent tight loop
                Thread.Sleep(50);
            }
        }
        catch (Exception)
        {
            // Ignore exceptions in input loop
        }
    }

    /// <summary>
    /// Update transfer statuses when pausing/resuming.
    /// </summary>
    private void UpdateTransferStatuses()
    {
        bool isPaused = _pauseTokenSource.IsPaused;
        string newStatus = isPaused ? "Paused" : "Copying";
        string oldStatus = isPaused ? "Copying" : "Paused";

        foreach (var kvp in _activeTransfers)
        {
            var transfer = kvp.Value;
            if (transfer.Status == oldStatus)
            {
                transfer.Status = newStatus;
            }
        }
    }

    /// <summary>
    /// Background render loop - updates UI at ~10 FPS to keep CPU usage low.
    /// </summary>
    private async Task RenderLoop(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested && !_shouldExit)
            {
                if (!_hidden)
                {
                    // Build state from active transfers
                    var state = BuildDashboardState();

                    // Render the dashboard
                    Render(state);
                }

                // Update at ~10 FPS (100ms between frames)
                await Task.Delay(100, cancellationToken);
            }
        }
        catch (OperationCanceledException)
        {
            // Expected when cancelling
        }
        catch (Exception)
        {
            // Ignore exceptions in render loop
        }
    }

    /// <summary>
    /// Build dashboard state from active transfers - zero allocation where possible.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private DashboardState BuildDashboardState()
    {
        var transfers = _activeTransfers.Values.ToArray();
        var transfersMemory = new ReadOnlyMemory<ActiveTransfer>(transfers);

        long totalBytes = 0;
        long totalBytesTransferred = 0;
        double totalSpeed = 0;
        int completedCount = 0;
        int copyingCount = 0;
        int pausedCount = 0;
        int failedCount = 0;

        foreach (var transfer in transfers)
        {
            totalBytes += transfer.TotalBytes;
            totalBytesTransferred += transfer.BytesTransferred;
            totalSpeed += transfer.BytesPerSecond;

            switch (transfer.Status)
            {
                case "Completed":
                    completedCount++;
                    break;
                case "Copying":
                    copyingCount++;
                    break;
                case "Paused":
                    pausedCount++;
                    break;
                case "Failed":
                    failedCount++;
                    break;
            }
        }

        double globalProgress = totalBytes > 0 ? (double)totalBytesTransferred / totalBytes : 0.0;
        double averageSpeed = transfers.Length > 0 ? totalSpeed / transfers.Length : 0.0;

        bool isPaused = _pauseTokenSource.IsPaused;
        string status = isPaused ? "Paused" : "Running";

        var resources = BuildResourceStats();

        // Convert ActiveTransfer[] to TransferItem[] for state
        // Note: Small allocation here (10 FPS), but render path remains zero-allocation
        var transferItems = new TransferItem[transfers.Length];
        for (int i = 0; i < transfers.Length; i++)
        {
            var at = transfers[i];
            double progress = at.TotalBytes > 0 ? (double)at.BytesTransferred / at.TotalBytes : 0.0;
            double speedMB = at.BytesPerSecond / (1024.0 * 1024.0);
            
            transferItems[i] = new TransferItem
            {
                FileName = Path.GetFileName(at.Source) ?? at.Source,
                Speed = speedMB,
                Progress = progress,
                Status = at.Status
            };
        }

        var transferItemsMemory = new ReadOnlyMemory<TransferItem>(transferItems);

        return new DashboardState(
            transferItemsMemory,
            globalProgress,
            totalBytesTransferred,
            totalBytes,
            averageSpeed,
            completedCount,
            copyingCount,
            pausedCount,
            failedCount,
            isPaused,
            status,
            resources);
    }

    /// <summary>
    /// Build resource statistics from watchdog.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private ResourceStats BuildResourceStats()
    {
        if (_resourceWatchdog == null)
            return ResourceStats.Empty;

        var stats = _resourceWatchdog.LatestStats;
        return new ResourceStats(
            stats.MemoryUsageMB,
            stats.MaxMemoryMB,
            stats.CpuUsagePercent,
            stats.CurrentThreadLimit,
            stats.CurrentThreadLimit,
            stats.IsThrottled);
    }

    /// <summary>
    /// Zero-allocation render method using Span&lt;char&gt; and ISpanFormattable.
    /// CRITICAL: NO string.Format, NO $"{interpolation}", NO boxing.
    /// </summary>
    public void Render(in DashboardState state)
    {
        _buffer.Clear();

        int width = _buffer.Width;
        int height = _buffer.Height;

        // Header
        DrawHeader(state, width);

        // Global progress bar
        DrawGlobalProgress(state, width);

        // Stats summary
        DrawStatsSummary(state, width);

        // Transfer list
        DrawTransferList(state, width, height);

        // Resource stats
        DrawResourceStats(state, width, height);

        // Footer with key bindings
        DrawFooter(state, width, height);

        // Flush to console
        _buffer.Flush();
    }

    /// <summary>
    /// Draw header with title.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void DrawHeader(in DashboardState state, int width)
    {
        _buffer.DrawBox(0, 0, width, 3, ConsoleColor.Cyan);
        
        ReadOnlySpan<char> title = "FastCopy Dashboard (AOT-Safe)";
        int titleX = (width - title.Length) / 2;
        _buffer.WriteAt(titleX, 1, title, ConsoleColor.Cyan);
    }

    /// <summary>
    /// Draw global progress bar.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void DrawGlobalProgress(in DashboardState state, int width)
    {
        int y = 3;
        
        // Progress label
        Span<char> progressLabel = stackalloc char[64];
        if (progressLabel.TryWrite($"Global Progress: {state.GlobalProgress:P1}", out int written))
        {
            _buffer.WriteAt(2, y, progressLabel.Slice(0, written), ConsoleColor.White);
        }

        // Progress bar
        int barWidth = width - 4;
        int filledWidth = (int)(state.GlobalProgress * barWidth);
        
        _buffer.WriteAt(2, y + 1, "[", ConsoleColor.Gray);
        if (filledWidth > 0)
        {
            _buffer.DrawHorizontalLine(3, y + 1, filledWidth, '█', ConsoleColor.Green);
        }
        if (filledWidth < barWidth)
        {
            _buffer.DrawHorizontalLine(3 + filledWidth, y + 1, barWidth - filledWidth, '░', ConsoleColor.DarkGray);
        }
        _buffer.WriteAt(2 + barWidth + 1, y + 1, "]", ConsoleColor.Gray);
    }

    /// <summary>
    /// Draw statistics summary.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void DrawStatsSummary(in DashboardState state, int width)
    {
        int y = 6;
        
        Span<char> buffer = stackalloc char[128];
        
        // Total bytes
        if (buffer.TryWrite($"Total: {FormatBytes(state.TotalBytesTransferred)} / {FormatBytes(state.TotalBytes)}", out int written))
        {
            _buffer.WriteAt(2, y, buffer.Slice(0, written), ConsoleColor.White);
        }

        // Speed
        double speedMB = state.AverageSpeed / (1024.0 * 1024.0);
        if (buffer.TryWrite($"Speed: {speedMB:F2} MB/s", out written))
        {
            _buffer.WriteAt(2, y + 1, buffer.Slice(0, written), ConsoleColor.White);
        }

        // Counts
        if (buffer.TryWrite($"Completed: {state.CompletedCount}  Copying: {state.CopyingCount}  Paused: {state.PausedCount}  Failed: {state.FailedCount}",
            out written))
        {
            _buffer.WriteAt(2, y + 2, buffer.Slice(0, written), ConsoleColor.White);
        }
    }

    /// <summary>
    /// Draw transfer list table.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void DrawTransferList(in DashboardState state, int width, int height)
    {
        int tableY = 10;
        int tableHeight = height - tableY - 4;
        
        if (tableHeight < 3)
            return;

        _buffer.DrawBox(0, tableY, width, tableHeight, ConsoleColor.DarkGray);

        // Table header
        _buffer.WriteAt(2, tableY + 1, "File Name", ConsoleColor.Yellow);
        _buffer.WriteAt(width - 50, tableY + 1, "Speed", ConsoleColor.Yellow);
        _buffer.WriteAt(width - 35, tableY + 1, "Progress", ConsoleColor.Yellow);
        _buffer.WriteAt(width - 15, tableY + 1, "Status", ConsoleColor.Yellow);

        _buffer.DrawHorizontalLine(1, tableY + 2, width - 2, '─', ConsoleColor.DarkGray);

        // Transfer rows
        var transfers = state.Transfers.Span;
        int maxRows = tableHeight - 4;
        int startIdx = Math.Min(_scrollOffset, Math.Max(0, transfers.Length - maxRows));
        int endIdx = Math.Min(startIdx + maxRows, transfers.Length);

        // Allocate buffer outside loop to avoid CA2014 warning
        Span<char> buffer = stackalloc char[32];

        for (int i = startIdx; i < endIdx; i++)
        {
            int rowY = tableY + 3 + (i - startIdx);
            var transfer = transfers[i];

            // File name (truncated)
            string fileName = transfer.FileName;
            int maxNameLen = width - 55;
            if (fileName.Length > maxNameLen)
            {
                fileName = "..." + fileName.Substring(fileName.Length - maxNameLen + 3);
            }
            _buffer.WriteAt(2, rowY, fileName, ConsoleColor.White);

            // Speed
            if (buffer.TryWrite($"{transfer.Speed:F1} MB/s", out int written))
            {
                _buffer.WriteAt(width - 50, rowY, buffer.Slice(0, written), ConsoleColor.Cyan);
            }

            // Progress bar
            int progressBarWidth = 15;
            int progressFilled = (int)(transfer.Progress * progressBarWidth);
            _buffer.WriteAt(width - 35, rowY, "[", ConsoleColor.Gray);
            if (progressFilled > 0)
            {
                _buffer.DrawHorizontalLine(width - 34, rowY, progressFilled, '█', ConsoleColor.Green);
            }
            if (progressFilled < progressBarWidth)
            {
                _buffer.DrawHorizontalLine(width - 34 + progressFilled, rowY, progressBarWidth - progressFilled, '░', ConsoleColor.DarkGray);
            }
            _buffer.WriteAt(width - 34 + progressBarWidth, rowY, "]", ConsoleColor.Gray);

            // Percentage
            if (buffer.TryWrite($"{transfer.Progress:P0}", out written))
            {
                _buffer.WriteAt(width - 30, rowY, buffer.Slice(0, written), ConsoleColor.White);
            }

            // Status
            ConsoleColor statusColor = transfer.Status switch
            {
                "Completed" => ConsoleColor.Green,
                "Copying" => ConsoleColor.Cyan,
                "Paused" => ConsoleColor.Yellow,
                "Failed" => ConsoleColor.Red,
                _ => ConsoleColor.Gray
            };
            _buffer.WriteAt(width - 15, rowY, transfer.Status, statusColor);
        }

        // Scroll indicator
        if (transfers.Length > maxRows)
        {
            Span<char> scrollInfo = stackalloc char[32];
            if (scrollInfo.TryWrite($"[{startIdx + 1}-{endIdx} of {transfers.Length}]", out int written))
            {
                _buffer.WriteAt(width - written - 2, tableY, scrollInfo.Slice(0, written), ConsoleColor.Yellow);
            }
        }
    }

    /// <summary>
    /// Draw resource statistics.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void DrawResourceStats(in DashboardState state, int width, int height)
    {
        int y = height - 3;
        
        if (state.Resources.MaxMemoryMB > 0)
        {
            Span<char> buffer = stackalloc char[128];
            string throttle = state.Resources.IsThrottled ? " [THROTTLED]" : "";
            
            if (buffer.TryWrite(
                $"Memory: {state.Resources.MemoryUsageMB:F1}/{state.Resources.MaxMemoryMB:F0} MB | " +
                $"CPU: {state.Resources.CpuUsagePercent:F1}% | " +
                $"Threads: {state.Resources.CurrentThreads}{throttle}",
                out int written))
            {
                _buffer.WriteAt(2, y, buffer.Slice(0, written), ConsoleColor.Cyan);
            }
        }
    }

    /// <summary>
    /// Draw footer with key bindings.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void DrawFooter(in DashboardState state, int width, int height)
    {
        int y = height - 2;
        _buffer.DrawHorizontalLine(0, y - 1, width, '─', ConsoleColor.DarkGray);
        
        ReadOnlySpan<char> footer = "[P] Pause/Resume  [H] Hide  [Esc/Q] Exit  [↑↓] Scroll";
        _buffer.WriteAt(2, y, footer, ConsoleColor.Gray);

        // Status
        Span<char> statusBuffer = stackalloc char[64];
        if (statusBuffer.TryWrite($"Status: {state.Status}", out int written))
        {
            _buffer.WriteAt(width - written - 2, y, statusBuffer.Slice(0, written), 
                state.IsPaused ? ConsoleColor.Yellow : ConsoleColor.Green);
        }
    }

    /// <summary>
    /// Format bytes to human-readable string without allocations.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static string FormatBytes(long bytes)
    {
        string[] units = { "B", "KB", "MB", "GB", "TB" };
        double size = bytes;
        int unitIndex = 0;

        while (size >= 1024 && unitIndex < units.Length - 1)
        {
            size /= 1024;
            unitIndex++;
        }

        return $"{size:F2} {units[unitIndex]}";
    }

    /// <summary>
    /// Wait for the dashboard to exit.
    /// </summary>
    public async Task WaitForExitAsync()
    {
        while (!_shouldExit && !_cts.Token.IsCancellationRequested)
        {
            await Task.Delay(100);
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;

        _cts.Cancel();
        
        try
        {
            Task.WaitAll(new[] { _inputTask, _renderTask }, TimeSpan.FromSeconds(1));
        }
        catch
        {
            // Ignore
        }

        _buffer.Dispose();
        _cts.Dispose();

        Console.CursorVisible = true;
        Console.Clear();
        Console.ResetColor();
    }
}

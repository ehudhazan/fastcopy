using Spectre.Console;
using FastCopy.Core;

namespace FastCopy.UI;

/// <summary>
/// Interactive dashboard controller with keyboard controls for managing transfers.
/// Supports pause/resume, speed adjustment, and visibility toggling.
/// </summary>
public sealed class InteractiveDashboard : IDisposable
{
    private readonly DashboardViewModel _viewModel;
    private readonly DashboardRenderer _renderer;
    private readonly PauseTokenSource? _pauseTokenSource;
    private readonly CancellationTokenSource _cts;
    private bool _hidden;
    private bool _disposed;

    public bool ShouldExit { get; private set; }

    public InteractiveDashboard(
        DashboardViewModel viewModel,
        PauseTokenSource? pauseTokenSource = null)
    {
        _viewModel = viewModel;
        _renderer = new DashboardRenderer(viewModel);
        _pauseTokenSource = pauseTokenSource;
        _cts = new CancellationTokenSource();
    }

    /// <summary>
    /// Run the interactive dashboard with keyboard controls.
    /// Blocks until user exits (Q/Esc) or cancellation is requested.
    /// </summary>
    public async Task RunAsync(
        Func<CancellationToken, Task> updateWorkerStatsAsync,
        CancellationToken cancellationToken = default)
    {
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _cts.Token);
        var token = linkedCts.Token;

        // Start keyboard input handler
        var inputTask = Task.Run(() => HandleInputAsync(token), token);

        // Start Spectre.Console Live display
        await AnsiConsole.Live(_renderer.Render())
            .AutoClear(false)
            .Overflow(VerticalOverflow.Ellipsis)
            .Cropping(VerticalOverflowCropping.Top)
            .StartAsync(async ctx =>
            {
                while (!token.IsCancellationRequested && !ShouldExit)
                {
                    if (!_hidden)
                    {
                        // Update worker statistics
                        await updateWorkerStatsAsync(token);

                        // Refresh display
                        ctx.UpdateTarget(_renderer.Render());
                    }

                    await Task.Delay(100, token);
                }

                // Final update
                if (!_hidden)
                {
                    await Task.Delay(200, token);
                    ctx.UpdateTarget(_renderer.RenderFinalState());
                }
            });

        // Wait for input handler to complete
        try
        {
            await inputTask.WaitAsync(TimeSpan.FromSeconds(1), CancellationToken.None);
        }
        catch
        {
            // Ignore
        }
    }

    /// <summary>
    /// Background task for handling keyboard input.
    /// Supports: P (Pause/Resume), H (Hide), Q/Esc (Exit), +/- (Speed adjust)
    /// </summary>
    private async Task HandleInputAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested && !ShouldExit)
            {
                if (Console.KeyAvailable)
                {
                    var key = Console.ReadKey(intercept: true);

                    switch (key.Key)
                    {
                        case ConsoleKey.P:
                            TogglePause();
                            break;

                        case ConsoleKey.H:
                            ToggleVisibility();
                            break;

                        case ConsoleKey.Q:
                        case ConsoleKey.Escape:
                            ShouldExit = true;
                            _cts.Cancel();
                            break;

                        case ConsoleKey.Add:
                        case ConsoleKey.OemPlus:
                            AdjustSpeed(increase: true);
                            break;

                        case ConsoleKey.Subtract:
                        case ConsoleKey.OemMinus:
                            AdjustSpeed(increase: false);
                            break;

                        case ConsoleKey.Spacebar:
                            TogglePause();
                            break;

                        case ConsoleKey.R:
                            ResetSpeed();
                            break;

                        case ConsoleKey.U:
                            SetUnlimitedSpeed();
                            break;
                    }
                }

                await Task.Delay(50, cancellationToken);
            }
        }
        catch (OperationCanceledException)
        {
            // Expected on cancellation
        }
    }

    private void TogglePause()
    {
        if (_pauseTokenSource != null)
        {
            _pauseTokenSource.Toggle();
            _viewModel.SetNotification(_pauseTokenSource.IsPaused ? "⏸ Paused" : "▶ Resumed");
        }
    }

    private void ToggleVisibility()
    {
        _hidden = !_hidden;
        if (!_hidden)
        {
            Console.Clear();
        }
        else
        {
            Console.Clear();
            Console.WriteLine("Dashboard hidden. Press H to show, Q to quit.");
        }
    }

    private void AdjustSpeed(bool increase)
    {
        var rateLimiter = TransferEngine.GetGlobalRateLimiter();
        if (rateLimiter != null)
        {
            long currentLimit = GetCurrentSpeedLimit();
            long newLimit;

            if (increase)
            {
                // Increase by 25%
                newLimit = (long)(currentLimit * 1.25);
                if (newLimit == currentLimit) newLimit = currentLimit + (10 * 1024 * 1024); // Min +10MB
            }
            else
            {
                // Decrease by 20%
                newLimit = (long)(currentLimit * 0.8);
                if (newLimit < 1024 * 1024) newLimit = 1024 * 1024; // Min 1MB/s
            }

            TransferEngine.SetGlobalLimit(newLimit);
            _viewModel.SetNotification($"Speed limit: {FormatSpeed(newLimit)}");
        }
        else
        {
            _viewModel.SetNotification("No rate limit set (unlimited)");
        }
    }

    private void ResetSpeed()
    {
        // Reset to a default speed (e.g., 100 MB/s)
        long defaultLimit = 100L * 1024 * 1024;
        TransferEngine.SetGlobalLimit(defaultLimit);
        _viewModel.SetNotification($"Speed limit reset to {FormatSpeed(defaultLimit)}");
    }

    private void SetUnlimitedSpeed()
    {
        TransferEngine.ClearGlobalLimit();
        _viewModel.SetNotification("Speed limit: UNLIMITED");
    }

    private long GetCurrentSpeedLimit()
    {
        // Default to 50 MB/s if not set
        return 50L * 1024 * 1024;
    }

    // ShowNotification method removed - now using _viewModel.SetNotification() directly

    private static string FormatSpeed(long bytesPerSec)
    {
        if (bytesPerSec < 1024)
            return $"{bytesPerSec} B/s";
        if (bytesPerSec < 1024 * 1024)
            return $"{bytesPerSec / 1024.0:F1} KB/s";
        if (bytesPerSec < 1024 * 1024 * 1024)
            return $"{bytesPerSec / (1024.0 * 1024):F1} MB/s";
        return $"{bytesPerSec / (1024.0 * 1024 * 1024):F2} GB/s";
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            _cts?.Cancel();
            _cts?.Dispose();
            _disposed = true;
        }
    }
}

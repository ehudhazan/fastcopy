using System.Collections.Concurrent;
using FastCopy.Core;
using FastCopy.UI;

namespace FastCopy;

/// <summary>
/// Demo program to showcase the AOT-safe ConsoleDashboard.
/// Run with: dotnet run --project FastCopy.csproj -- --demo-dashboard
/// </summary>
public static class DashboardDemo
{
    public static async Task RunAsync(CancellationToken cancellationToken = default)
    {
        Console.WriteLine("Starting FastCopy Dashboard Demo...");
        Console.WriteLine("This demo shows the AOT-safe TUI with mock file transfers.");
        Console.WriteLine("Press any key to start...");
        Console.ReadKey(true);

        // Create mock data structures
        var activeTransfers = new ConcurrentDictionary<string, ActiveTransfer>();
        var pauseTokenSource = new PauseTokenSource();

        // Add some mock transfers
        for (int i = 0; i < 20; i++)
        {
            var transfer = new ActiveTransfer
            {
                Source = $"/source/path/to/file_{i:0000}.dat",
                Destination = $"/dest/path/to/file_{i:0000}.dat",
                TotalBytes = (long)(100 * 1024 * 1024 * (1 + i * 0.5)), // Varying sizes
                BytesTransferred = 0,
                BytesPerSecond = 0,
                Status = "Copying"
            };
            activeTransfers.TryAdd(transfer.Source, transfer);
        }

        // Create dashboard
        using var dashboard = new ConsoleDashboard(
            activeTransfers,
            pauseTokenSource,
            resourceWatchdog: null);

        // Simulate file transfers in background
        var simulationTask = Task.Run(async () =>
        {
            var random = new Random();
            
            while (!cancellationToken.IsCancellationRequested && !dashboard.ShouldExit)
            {
                foreach (var kvp in activeTransfers)
                {
                    var transfer = kvp.Value;
                    
                    if (transfer.Status == "Copying" && !pauseTokenSource.IsPaused)
                    {
                        // Simulate random progress
                        long increment = (long)(random.Next(1, 10) * 1024 * 1024 * 2); // 2-20 MB
                        transfer.BytesTransferred = Math.Min(
                            transfer.BytesTransferred + increment,
                            transfer.TotalBytes);
                        
                        // Random speed between 10-150 MB/s
                        transfer.BytesPerSecond = random.Next(10, 150) * 1024 * 1024;

                        // Mark as completed if done
                        if (transfer.BytesTransferred >= transfer.TotalBytes)
                        {
                            transfer.Status = "Completed";
                            transfer.BytesPerSecond = 0;
                        }
                    }
                }

                await Task.Delay(100, cancellationToken);
            }
        }, cancellationToken);

        // Wait for user to exit
        await dashboard.WaitForExitAsync();

        Console.WriteLine("\nDashboard demo completed!");
        Console.WriteLine("Press any key to exit...");
        
        // Cancel simulation
        try
        {
            await simulationTask.WaitAsync(TimeSpan.FromSeconds(1));
        }
        catch
        {
            // Ignore
        }
    }
}

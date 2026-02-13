using System.Data;
using Terminal.Gui;
using FastCopy.Core;

namespace FastCopy.UI;

public sealed class Dashboard : Window
{
    private TableView _tableView = null!;
    private ProgressBar _globalProgressBar = null!;
    private Label _globalStatusLabel = null!;
    private Label _footerLabel = null!;
    private Label _resourceStatsLabel = null!;
    private readonly List<TransferItem> _items;
    private readonly PauseTokenSource _pauseTokenSource;
    private readonly ResourceWatchdog? _resourceWatchdog;
    private bool _quietMode = false;
    private bool _showUI = true;
    private Timer? _updateTimer;
    private Timer? _consoleTimer;
    private Timer? _resourceTimer;
    private DateTime _lastConsoleUpdate = DateTime.MinValue;

    public Dashboard(bool showUI = true, ResourceWatchdog? resourceWatchdog = null)
    {
        Title = "FastCopy Dashboard (V2)";
        _showUI = showUI;
        _resourceWatchdog = resourceWatchdog;
        
        // Initialize data
        _items = GenerateMockData(10000);
        _pauseTokenSource = new PauseTokenSource();

        // Setup Views
        SetupDetailedView();
        SetupQuietView();
        SetupResourceStats();
        SetupFooter();

        // Key Bindings
        // V2 uses Key bindings differently, but AddKeyBinding or HotKey is common.
        // We can override OnKeyDown or use Command.
        
        // P for Pause
        KeyDown += (s, e) =>
        {
            if (e == Key.P)
            {
                TogglePause();
                e.Handled = true;
            }
            else if (e == Key.T) // Toggle Stats
            {
                ToggleView();
                e.Handled = true;
            }
            else if (e == Key.H) // Toggle UI Display (Headless)
            {
                ToggleUIDisplay();
                e.Handled = true;
            }
        };

        // Start update timer (simulating 10 updates per second)
        _updateTimer = new Timer(_ =>
        {
            if (_pauseTokenSource.IsPaused) return;

            // Update UI on main thread only if UI is shown
            if (_showUI)
            {
                Application.Invoke(() =>
                {
                    UpdateMockProgress();
                    if (_quietMode)
                    {
                        UpdateGlobalProgress();
                    }
                    SetNeedsDraw();
                });
            }
            else
            {
                // In headless mode, just update the data without UI refresh
                UpdateMockProgress();
            }
        }, null, 0, 100);
        
        // Start console timer for headless mode (every 5 seconds)
        _consoleTimer = new Timer(_ =>
        {
            if (!_showUI && !_pauseTokenSource.IsPaused)
            {
                OutputConsoleStatus();
            }
        }, null, 0, 5000);
        
        // Start resource stats timer (every 500ms)
        if (_resourceWatchdog != null)
        {
            _resourceTimer = new Timer(_ =>
            {
                if (_showUI)
                {
                    Application.Invoke(() =>
                    {
                        UpdateResourceStats();
                        SetNeedsDraw();
                    });
                }
                else
                {
                    UpdateResourceStats();
                }
            }, null, 0, 500);
        }
    }

    protected override void Dispose(bool disposing)
    {
        _updateTimer?.Dispose();
        _consoleTimer?.Dispose();
        _resourceTimer?.Dispose();
        base.Dispose(disposing);
    }

    private void UpdateMockProgress()
    {
        // Simulate progress for some active items
        // In a real app, this would be driven by events or checking the TransferManager state
        var activeItems = _items.Where(i => i.Status == "Copying").ToList();
        
        // Randomly update some items to simulate concurrency
        var rnd = new Random();
        foreach (var item in activeItems)
        {
            if (rnd.NextDouble() > 0.7) // Update 30% of active items each tick
            {
                double increment = (item.Speed * 1024 * 1024 / 10.0) / (100 * 1024 * 1024); // Fake size
                // Simplify: just add random progress
                item.Progress += 0.01 + (rnd.NextDouble() * 0.02);
                
                if (item.Progress >= 1.0)
                {
                    item.Progress = 1.0;
                    item.Status = "Completed";
                }
            }
        }
    }

    private void SetupDetailedView()
    {
        _tableView = new TableView
        {
            X = 0,
            Y = 0,
            Width = Dim.Fill(),
            Height = Dim.Fill(2), // Leave room for footer
            Table = new TransferItemTableSource(_items)
        };
        
        Add(_tableView);
    }

    private void SetupQuietView()
    {
        // Quiet view is hidden by default
        _globalProgressBar = new ProgressBar
        {
            X = Pos.Center(),
            Y = Pos.Center(),
            Width = Dim.Percent(50),
            Height = 1, // ProgressBar height
            Visible = false
        };

        _globalStatusLabel = new Label
        {
            X = Pos.Center(),
            Y = Pos.Center() - 2,
            Text = "Global Progress",
            Visible = false
        };

        Add(_globalStatusLabel, _globalProgressBar);
    }

    private void SetupResourceStats()
    {
        _resourceStatsLabel = new Label
        {
            X = 0,
            Y = Pos.AnchorEnd(2),
            Width = Dim.Fill(),
            Height = 1,
            Text = "Resource Monitor: Not available"
        };
        Add(_resourceStatsLabel);
    }

    private void UpdateResourceStats()
    {
        if (_resourceWatchdog == null)
        {
            _resourceStatsLabel.Text = "Resource Monitor: Disabled";
            return;
        }

        var stats = _resourceWatchdog.LatestStats;
        string throttleIndicator = stats.IsThrottled ? " [THROTTLED]" : "";
        string maxMemText = stats.MaxMemoryMB > 0 ? $" / {stats.MaxMemoryMB:F0} MB" : "";
        
        _resourceStatsLabel.Text = $" Memory: {stats.MemoryUsageMB:F1} MB{maxMemText} | " +
                                   $"CPU: {stats.CpuUsagePercent:F1}% | " +
                                   $"Threads: {stats.CurrentThreadLimit}{throttleIndicator}";
    }

    private void SetupFooter()
    {
        _footerLabel = new Label
        {
            X = 0,
            Y = Pos.AnchorEnd(1),
            Width = Dim.Fill(),
            Height = 1,
            Text = " [P] Pause/Resume  [T] Toggle Stats  [H] Hide UI | Status: Running"
        };
        Add(_footerLabel);
    }

    private void TogglePause()
    {
        _pauseTokenSource.Toggle();
        bool isPaused = _pauseTokenSource.IsPaused;
        UpdateStatus(isPaused ? "Paused" : "Running");

        if (isPaused)
        {
            foreach (var item in _items.Where(i => i.Status == "Copying"))
            {
                item.Status = "Paused";
            }
        }
        else
        {
            foreach (var item in _items.Where(i => i.Status == "Paused"))
            {
                item.Status = "Copying";
            }
        }
        _tableView.SetNeedsDraw();
    }

    private void ToggleView()
    {
        _quietMode = !_quietMode;
        
        if (_quietMode)
        {
            _tableView.Visible = false;
            _globalProgressBar.Visible = true;
            _globalStatusLabel.Visible = true;
            
            // Calculate global progress
            UpdateGlobalProgress();
        }
        else
        {
            _tableView.Visible = true;
            _globalProgressBar.Visible = false;
            _globalStatusLabel.Visible = false;
        }
        
        SetNeedsDraw();
    }

    private void UpdateStatus(string status)
    {
        _footerLabel.Text = $" [P] Pause/Resume  [T] Toggle Stats  [H] Hide UI | Status: {status}";
    }

    private void UpdateGlobalProgress()
    {
        if (_items.Count == 0) return;
        
        double totalProgress = _items.Average(i => i.Progress);
        _globalProgressBar.Fraction = (float)totalProgress;
        _globalStatusLabel.Text = $"Total Progress: {totalProgress:P1}";
    }

    private void ToggleUIDisplay()
    {
        _showUI = !_showUI;
        
        if (!_showUI)
        {
            // Hide all UI elements to save CPU cycles
            _tableView.Visible = false;
            _globalProgressBar.Visible = false;
            _globalStatusLabel.Visible = false;
            _footerLabel.Text = " UI HIDDEN - Press [H] to show UI again";
            UpdateStatus("Hidden (Press H to show)");
        }
        else
        {
            // Show UI elements again
            if (_quietMode)
            {
                _globalProgressBar.Visible = true;
                _globalStatusLabel.Visible = true;
            }
            else
            {
                _tableView.Visible = true;
            }
            
            UpdateStatus(_pauseTokenSource.IsPaused ? "Paused" : "Running");
        }
        
        SetNeedsDraw();
    }

    private void OutputConsoleStatus()
    {
        if (_items.Count == 0) return;
        
        int completed = _items.Count(i => i.Status == "Completed");
        int copying = _items.Count(i => i.Status == "Copying");
        int paused = _items.Count(i => i.Status == "Paused");
        double avgProgress = _items.Average(i => i.Progress);
        double avgSpeed = _items.Where(i => i.Status == "Copying").DefaultIfEmpty(new TransferItem()).Average(i => i.Speed);
        
        var timestamp = DateTime.Now.ToString("HH:mm:ss");
        var statusLine = $"[{timestamp}] Progress: {avgProgress:P1} | Copying: {copying} | Completed: {completed} | Paused: {paused} | Avg Speed: {avgSpeed:F1} MB/s";
        
        // Add resource stats if watchdog is available
        if (_resourceWatchdog != null)
        {
            var stats = _resourceWatchdog.LatestStats;
            string throttleIndicator = stats.IsThrottled ? " [THROTTLED]" : "";
            statusLine += $" | RAM: {stats.MemoryUsageMB:F1} MB | CPU: {stats.CpuUsagePercent:F1}% | Threads: {stats.CurrentThreadLimit}{throttleIndicator}";
        }
        
        Console.WriteLine(statusLine);
    }

    private List<TransferItem> GenerateMockData(int count)
    {
        var list = new List<TransferItem>(count);
        var rnd = new Random();
        
        for (int i = 0; i < count; i++)
        {
            list.Add(new TransferItem
            {
                FileName = $"file_{i:0000}.dat",
                Speed = rnd.Next(10, 500), // MB/s
                Progress = rnd.NextDouble(),
                Status = i < count / 2 ? "Completed" : "Copying"
            });
        }
        return list;
    }
}


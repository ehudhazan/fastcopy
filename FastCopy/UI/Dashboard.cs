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
    private readonly List<TransferItem> _items;
    private readonly PauseTokenSource _pauseTokenSource;
    private bool _quietMode = false;
    private Timer? _updateTimer;

    public Dashboard()
    {
        Title = "FastCopy Dashboard (V2)";
        
        // Initialize data
        _items = GenerateMockData(10000);
        _pauseTokenSource = new PauseTokenSource();

        // Setup Views
        SetupDetailedView();
        SetupQuietView();
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
        };

        // Start update timer (simulating 10 updates per second)
        _updateTimer = new Timer(_ =>
        {
            if (_pauseTokenSource.IsPaused) return;

            // Update UI on main thread
            Application.Invoke(() =>
            {
                UpdateMockProgress();
                if (_quietMode)
                {
                    UpdateGlobalProgress();
                }
                SetNeedsDraw();
            });
        }, null, 0, 100);
    }

    protected override void Dispose(bool disposing)
    {
        _updateTimer?.Dispose();
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

    private void SetupFooter()
    {
        _footerLabel = new Label
        {
            X = 0,
            Y = Pos.AnchorEnd(1),
            Width = Dim.Fill(),
            Height = 1,
            Text = " [P] Pause/Resume  [T] Toggle Stats | Status: Running",
            ColorScheme = Colors.ColorSchemes["Menu"]
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
        _footerLabel.Text = $" [P] Pause/Resume  [T] Toggle Stats | Status: {status}";
    }

    private void UpdateGlobalProgress()
    {
        if (_items.Count == 0) return;
        
        double totalProgress = _items.Average(i => i.Progress);
        _globalProgressBar.Fraction = (float)totalProgress;
        _globalStatusLabel.Text = $"Total Progress: {totalProgress:P1}";
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


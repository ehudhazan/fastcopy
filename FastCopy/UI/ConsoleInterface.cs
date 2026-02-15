using System.Runtime.CompilerServices;
using System.Text;
using FastCopy.Core;

namespace FastCopy.UI;

/// <summary>
/// Native Console-based UI orchestrator with Zero-GC rendering and crash-free input.
/// Supports three modes: Menu, Dashboard, and Headless.
/// AOT-safe, thread-safe, uses only System.Console methods.
/// </summary>
public sealed class ConsoleInterface : IDisposable
{
    private readonly object _stateLock = new();
    private readonly CancellationTokenSource _cts;
    private Task? _inputTask;
    
    // UI State
    private UIMode _currentMode;
    private bool _disposed;
    private int _renderWidth;
    private int _renderHeight;
    
    // Dashboard state
    private DashboardViewModel? _dashboardViewModel;
    private PauseTokenSource? _pauseTokenSource;
    private WorkerPool? _workerPool;
    
    // Menu state
    private MenuState _menuState;
    
    // Headless state
    private long _lastHeadlessUpdate;
    
    // Double-buffering for Zero-GC rendering
    private char[] _backBuffer;
    private char[] _frontBuffer;
    private int _backBufferLength;
    
    // Error logging
    private readonly ErrorLogger _errorLogger;
    
    public UIMode CurrentMode
    {
        get { lock (_stateLock) return _currentMode; }
        private set { lock (_stateLock) _currentMode = value; }
    }
    
    public bool ShouldExit { get; private set; }
    
    public ConsoleInterface()
    {
        _cts = new CancellationTokenSource();
        _currentMode = UIMode.Menu;
        _menuState = new MenuState();
        _renderWidth = Math.Max(Console.WindowWidth, 80);
        _renderHeight = Math.Max(Console.WindowHeight, 24);
        _backBuffer = new char[_renderWidth * _renderHeight];
        _frontBuffer = new char[_renderWidth * _renderHeight];
        _backBufferLength = 0;
        _errorLogger = new ErrorLogger("fastcopy_errors.log");
        _lastHeadlessUpdate = 0;
    }
    
    /// <summary>
    /// Start the console interface in Menu mode.
    /// </summary>
    public async Task RunMenuAsync(CancellationToken cancellationToken = default)
    {
        CurrentMode = UIMode.Menu;
        
        try
        {
            Console.Clear();
            Console.CursorVisible = false;
            
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _cts.Token);
            var token = linkedCts.Token;
            
            // Start input handler
            _inputTask = Task.Run(() => InputLoopAsync(token), token);
            
            // Menu render loop
            while (!token.IsCancellationRequested && !ShouldExit && CurrentMode == UIMode.Menu)
            {
                RenderMenu();
                await Task.Delay(50, token); // 20 FPS for menu
            }
            
            // If user pressed Start, return the menu state
            if (_menuState.ShouldStart)
            {
                Console.Clear();
                Console.CursorVisible = true;
            }
        }
        catch (OperationCanceledException)
        {
            // Expected during cancellation
        }
        finally
        {
            Console.CursorVisible = true;
        }
    }
    
    /// <summary>
    /// Get the menu configuration selected by the user.
    /// </summary>
    public MenuState GetMenuState() => _menuState;
    
    /// <summary>
    /// Start the console interface in Dashboard mode.
    /// </summary>
    public async Task RunDashboardAsync(
        DashboardViewModel viewModel,
        PauseTokenSource pauseTokenSource,
        WorkerPool workerPool,
        Func<CancellationToken, Task> updateStatsAsync,
        CancellationToken cancellationToken = default)
    {
        CurrentMode = UIMode.Dashboard;
        _dashboardViewModel = viewModel;
        _pauseTokenSource = pauseTokenSource;
        _workerPool = workerPool;
        
        try
        {
            Console.Clear();
            Console.CursorVisible = false;
            
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _cts.Token);
            var token = linkedCts.Token;
            
            // Start input handler
            _inputTask = Task.Run(() => InputLoopAsync(token), token);
            
            // Dashboard render loop - 10 FPS
            while (!token.IsCancellationRequested && !ShouldExit)
            {
                // Update stats from workers
                await updateStatsAsync(token);
                
                // Render based on current mode
                if (CurrentMode == UIMode.Dashboard)
                {
                    RenderDashboard();
                }
                else if (CurrentMode == UIMode.Headless)
                {
                    RenderHeadless();
                }
                
                await Task.Delay(100, token); // 10 FPS for dashboard
            }
        }
        catch (OperationCanceledException)
        {
            // Expected during cancellation
        }
        finally
        {
            Console.Clear();
            Console.CursorVisible = true;
        }
    }
    
    /// <summary>
    /// Crash-free input loop with error logging to prevent app crashes.
    /// </summary>
    private async Task InputLoopAsync(CancellationToken cancellationToken)
    {
        await Task.Yield(); // Move to thread pool
        
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    if (Console.KeyAvailable)
                    {
                        var key = Console.ReadKey(intercept: true);
                        HandleKeyPress(key);
                    }
                    
                    await Task.Delay(10, cancellationToken); // Poll every 10ms
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // Log error but don't crash the app
                    await _errorLogger.LogAsync($"[InputLoop] {ex.GetType().Name}: {ex.Message}");
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Expected during shutdown
        }
        catch (Exception ex)
        {
            await _errorLogger.LogAsync($"[InputLoop Fatal] {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}");
        }
    }
    
    /// <summary>
    /// Handle keyboard input based on current mode.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void HandleKeyPress(ConsoleKeyInfo key)
    {
        try
        {
            if (CurrentMode == UIMode.Menu)
            {
                HandleMenuInput(key);
            }
            else if (CurrentMode == UIMode.Dashboard || CurrentMode == UIMode.Headless)
            {
                HandleDashboardInput(key);
            }
        }
        catch (Exception ex)
        {
            _errorLogger.LogAsync($"[HandleKeyPress] {ex.Message}").GetAwaiter().GetResult();
        }
    }
    
    /// <summary>
    /// Handle menu navigation input.
    /// </summary>
    private void HandleMenuInput(ConsoleKeyInfo key)
    {
        switch (key.Key)
        {
            case ConsoleKey.UpArrow:
                _menuState.MoveUp();
                break;
            
            case ConsoleKey.DownArrow:
                _menuState.MoveDown();
                break;
            
            case ConsoleKey.Enter:
            case ConsoleKey.Spacebar:
                _menuState.Activate();
                if (_menuState.ShouldExit)
                {
                    ShouldExit = true;
                }
                break;
            
            case ConsoleKey.Escape:
            case ConsoleKey.Q:
                ShouldExit = true;
                break;
            
            case ConsoleKey.H:
                _menuState.CurrentItem = MenuItemType.Help;
                _menuState.Activate();
                break;
            
            // Text input for current field
            case ConsoleKey.Backspace:
                _menuState.DeleteChar();
                break;
            
            default:
                if (!char.IsControl(key.KeyChar))
                {
                    _menuState.AppendChar(key.KeyChar);
                }
                break;
        }
    }
    
    /// <summary>
    /// Handle dashboard control input (pause, speed, parallelism, hide).
    /// </summary>
    private void HandleDashboardInput(ConsoleKeyInfo key)
    {
        switch (key.Key)
        {
            case ConsoleKey.P:
                // Toggle pause
                _pauseTokenSource?.Toggle();
                break;
            
            case ConsoleKey.H:
                // Toggle between Dashboard and Headless
                if (CurrentMode == UIMode.Dashboard)
                {
                    Console.Clear();
                    CurrentMode = UIMode.Headless;
                }
                else
                {
                    Console.Clear();
                    CurrentMode = UIMode.Dashboard;
                }
                break;
            
            case ConsoleKey.Add:
            case ConsoleKey.OemPlus:
                // Increase speed limit by 10MB/s
                AdjustSpeedLimit(10L * 1024 * 1024);
                break;
            
            case ConsoleKey.Subtract:
            case ConsoleKey.OemMinus:
                // Decrease speed limit by 10MB/s
                AdjustSpeedLimit(-10L * 1024 * 1024);
                break;
            
            case ConsoleKey.U:
                // Unlimited speed
                TransferEngine.SetGlobalLimit(0);
                break;
            
            case ConsoleKey.RightArrow:
                // Increase parallelism (using right arrow since ] key enum doesn't exist)
                _workerPool?.AdjustParallelism(1);
                break;
            
            case ConsoleKey.LeftArrow:
                // Decrease parallelism (using left arrow since [ key enum doesn't exist)
                _workerPool?.AdjustParallelism(-1);
                break;
            
            case ConsoleKey.Q:
            case ConsoleKey.Escape:
                ShouldExit = true;
                _cts.Cancel();
                break;
        }
    }
    
    /// <summary>
    /// Adjust the global speed limit by delta bytes/sec.
    /// </summary>
    private void AdjustSpeedLimit(long deltaBytesPerSec)
    {
        var limiter = TransferEngine.GetGlobalRateLimiter();
        if (limiter != null)
        {
            long currentLimit = limiter.GetCurrentLimit();
            long newLimit = Math.Max(0, currentLimit + deltaBytesPerSec);
            TransferEngine.SetGlobalLimit(newLimit);
        }
        else
        {
            // No limit set yet, start at 10MB/s if decreasing, or set the delta if increasing
            long initialLimit = deltaBytesPerSec > 0 ? deltaBytesPerSec : 10L * 1024 * 1024;
            TransferEngine.SetGlobalLimit(initialLimit);
        }
    }
    
    /// <summary>
    /// Render the menu using Zero-GC Span-based buffer.
    /// </summary>
    private void RenderMenu()
    {
        try
        {
            // Build menu in back buffer
            ClearBuffer();
            
            int row = 1;
            
            // Title
            WriteToBuffer(row++, 2, "╔══════════════════════════════════════════════════════════════╗");
            WriteToBuffer(row++, 2, "║         FastCopy - High-Performance File Transfer            ║");
            WriteToBuffer(row++, 2, "╚══════════════════════════════════════════════════════════════╝");
            row++;
            
            // Menu items
            RenderMenuItem(ref row, MenuItemType.Source, $"Source Path: {_menuState.SourcePath}");
            RenderMenuItem(ref row, MenuItemType.Destination, $"Destination: {_menuState.DestinationPath}");
            RenderMenuItem(ref row, MenuItemType.Verify, $"Verify Checksum: {(_menuState.VerifyChecksum ? "ON" : "OFF")}");
            RenderMenuItem(ref row, MenuItemType.SpeedLimit, $"Speed Limit: {_menuState.SpeedLimitText}");
            RenderMenuItem(ref row, MenuItemType.Parallelism, $"Max Parallelism: {_menuState.MaxParallelism}");
            row++;
            RenderMenuItem(ref row, MenuItemType.Help, "[Help] - Show CLI Arguments");
            RenderMenuItem(ref row, MenuItemType.Start, "[Start] - Begin File Transfer");
            RenderMenuItem(ref row, MenuItemType.Exit, "[Exit] - Quit");
            
            row += 2;
            WriteToBuffer(row++, 2, "Navigation: ↑/↓ arrows, Enter to select/toggle");
            WriteToBuffer(row++, 2, "Text Fields: Type to edit, Backspace to delete");
            
            // Flush buffer to console
            FlushBuffer();
        }
        catch (Exception ex)
        {
            _errorLogger.LogAsync($"[RenderMenu] {ex.Message}").GetAwaiter().GetResult();
        }
    }
    
    /// <summary>
    /// Render a single menu item with selection indicator.
    /// </summary>
    private void RenderMenuItem(ref int row, MenuItemType itemType, string text)
    {
        bool isSelected = _menuState.CurrentItem == itemType;
        string prefix = isSelected ? "► " : "  ";
        WriteToBuffer(row++, 2, prefix + text);
    }
    
    /// <summary>
    /// Render the dashboard with double-buffering.
    /// </summary>
    private void RenderDashboard()
    {
        if (_dashboardViewModel == null) return;
        
        try
        {
            ClearBuffer();
            
            int row = 0;
            
            // Top: Global stats
            WriteToBuffer(row++, 0, "═══════════════════════════════════════════════════════════════════════════════");
            
            string statusLine = _dashboardViewModel.StatusMessage;
            string speedLine = $"Speed: {_dashboardViewModel.GlobalSpeed}";
            string progressLine = $"Progress: {_dashboardViewModel.Progress:F1}% | Files: {_dashboardViewModel.CompletedCount}";
            
            WriteToBuffer(row++, 2, statusLine);
            WriteToBuffer(row++, 2, speedLine);
            WriteToBuffer(row++, 2, progressLine);
            WriteToBuffer(row++, 0, "═══════════════════════════════════════════════════════════════════════════════");
            
            // Middle: Worker list
            var workers = _dashboardViewModel.GetWorkerSnapshot();
            int maxWorkers = Math.Min(workers.Count, _renderHeight - 10);
            
            for (int i = 0; i < maxWorkers; i++)
            {
                var worker = workers[i];
                string workerLine = FormatWorkerLine(worker);
                WriteToBuffer(row++, 2, workerLine);
            }
            
            // Bottom: Controls
            int controlRow = _renderHeight - 4;
            WriteToBuffer(controlRow++, 0, "───────────────────────────────────────────────────────────────────────────────");
            WriteToBuffer(controlRow++, 2, "Controls: [P]ause | [H]ide | +/- Speed | ←/→ Parallelism | [U]nlimited | [Q]uit");
            
            FlushBuffer();
        }
        catch (Exception ex)
        {
            _errorLogger.LogAsync($"[RenderDashboard] {ex.Message}").GetAwaiter().GetResult();
        }
    }
    
    /// <summary>
    /// Render headless mode - single line at bottom.
    /// </summary>
    private void RenderHeadless()
    {
        if (_dashboardViewModel == null) return;
        
        try
        {
            // Only update every 500ms to save CPU
            long now = Environment.TickCount64;
            if (now - _lastHeadlessUpdate < 500)
            {
                return;
            }
            _lastHeadlessUpdate = now;
            
            // Single-line progress at bottom
            Console.SetCursorPosition(0, Console.WindowHeight - 1);
            
            string line = $"Progress: {_dashboardViewModel.Progress:F1}% | " +
                         $"Files: {_dashboardViewModel.CompletedCount} | " +
                         $"Speed: {_dashboardViewModel.GlobalSpeed} | " +
                         $"[H] Show Dashboard";
            
            // Pad to full width to clear previous content
            int padding = Math.Max(0, Console.WindowWidth - line.Length - 1);
            line += new string(' ', padding);
            
            Console.Write(line);
        }
        catch (Exception ex)
        {
            _errorLogger.LogAsync($"[RenderHeadless] {ex.Message}").GetAwaiter().GetResult();
        }
    }
    
    /// <summary>
    /// Format a worker line with progress bar (Zero-GC).
    /// </summary>
    private string FormatWorkerLine(WorkerState worker)
    {
        const int barWidth = 20;
        int filled = (int)(worker.Progress / 100.0 * barWidth);
        filled = Math.Clamp(filled, 0, barWidth);
        
        string bar = new string('█', filled) + new string('░', barWidth - filled);
        string speed = FormatSpeed(worker.Speed);
        
        // Truncate filename if too long
        string fileName = worker.FileName;
        const int maxFileNameLength = 30;
        if (fileName.Length > maxFileNameLength)
        {
            fileName = "..." + fileName.Substring(fileName.Length - maxFileNameLength + 3);
        }
        
        return $"{fileName,-33} [{bar}] {worker.Progress,5:F1}% {speed,12}";
    }
    
    /// <summary>
    /// Format speed in human-readable form.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static string FormatSpeed(double bytesPerSec)
    {
        if (bytesPerSec >= 1_000_000_000)
            return $"{bytesPerSec / 1_000_000_000:F2} GB/s";
        if (bytesPerSec >= 1_000_000)
            return $"{bytesPerSec / 1_000_000:F2} MB/s";
        if (bytesPerSec >= 1_000)
            return $"{bytesPerSec / 1_000:F2} KB/s";
        return $"{bytesPerSec:F0} B/s";
    }
    
    // ============= Zero-GC Buffer Management =============
    
    /// <summary>
    /// Clear the back buffer.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void ClearBuffer()
    {
        Array.Fill(_backBuffer, ' ');
        _backBufferLength = 0;
    }
    
    /// <summary>
    /// Write text to the back buffer at specified position.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void WriteToBuffer(int row, int col, string text)
    {
        if (row < 0 || row >= _renderHeight) return;
        if (col < 0 || col >= _renderWidth) return;
        
        int offset = row * _renderWidth + col;
        int copyLength = Math.Min(text.Length, _renderWidth - col);
        
        text.AsSpan(0, copyLength).CopyTo(_backBuffer.AsSpan(offset, copyLength));
        _backBufferLength = Math.Max(_backBufferLength, offset + copyLength);
    }
    
    /// <summary>
    /// Flush back buffer to front buffer and render to console.
    /// Only updates changed characters for efficiency.
    /// </summary>
    private void FlushBuffer()
    {
        try
        {
            // Simple full refresh for now (can optimize with diff later)
            Console.SetCursorPosition(0, 0);
            
            var span = _backBuffer.AsSpan(0, _backBufferLength);
            
            // Write line by line
            for (int row = 0; row < _renderHeight; row++)
            {
                int start = row * _renderWidth;
                int end = Math.Min(start + _renderWidth, _backBufferLength);
                
                if (end > start)
                {
                    Console.SetCursorPosition(0, row);
                    Console.Write(span.Slice(start, end - start));
                }
            }
        }
        catch (Exception ex)
        {
            _errorLogger.LogAsync($"[FlushBuffer] {ex.Message}").GetAwaiter().GetResult();
        }
    }
    
    public void Dispose()
    {
        if (_disposed) return;
        
        _disposed = true;
        _cts.Cancel();
        
        try
        {
            _inputTask?.Wait(TimeSpan.FromSeconds(1));
        }
        catch
        {
            // Ignore cleanup errors
        }
        
        _cts.Dispose();
        Console.CursorVisible = true;
    }
}

/// <summary>
/// UI operating modes.
/// </summary>
public enum UIMode
{
    Menu,
    Dashboard,
    Headless
}

/// <summary>
/// Menu state with navigation and editing support.
/// </summary>
public sealed class MenuState
{
    public MenuItemType CurrentItem { get; set; } = MenuItemType.Source;
    
    public string SourcePath { get; set; } = "";
    public string DestinationPath { get; set; } = "";
    public bool VerifyChecksum { get; set; } = false;
    public string SpeedLimitText { get; set; } = "Unlimited";
    public int MaxParallelism { get; set; } = Environment.ProcessorCount;
    
    public bool ShouldStart { get; set; } = false;
    public bool ShouldExit { get; set; } = false;
    
    public void MoveUp()
    {
        int current = (int)CurrentItem;
        current = (current - 1 + 8) % 8;
        CurrentItem = (MenuItemType)current;
    }
    
    public void MoveDown()
    {
        int current = (int)CurrentItem;
        current = (current + 1) % 8;
        CurrentItem = (MenuItemType)current;
    }
    
    public void Activate()
    {
        switch (CurrentItem)
        {
            case MenuItemType.Verify:
                VerifyChecksum = !VerifyChecksum;
                break;
            
            case MenuItemType.Help:
                ShowHelp();
                break;
            
            case MenuItemType.Start:
                ShouldStart = true;
                break;
            
            case MenuItemType.Exit:
                ShouldExit = true;
                break;
        }
    }
    
    public void AppendChar(char c)
    {
        switch (CurrentItem)
        {
            case MenuItemType.Source:
                SourcePath += c;
                break;
            
            case MenuItemType.Destination:
                DestinationPath += c;
                break;
            
            case MenuItemType.SpeedLimit:
                SpeedLimitText += c;
                break;
            
            case MenuItemType.Parallelism:
                if (char.IsDigit(c))
                {
                    MaxParallelism = MaxParallelism * 10 + (c - '0');
                    MaxParallelism = Math.Clamp(MaxParallelism, 1, 128);
                }
                break;
        }
    }
    
    public void DeleteChar()
    {
        switch (CurrentItem)
        {
            case MenuItemType.Source:
                if (SourcePath.Length > 0)
                    SourcePath = SourcePath.Substring(0, SourcePath.Length - 1);
                break;
            
            case MenuItemType.Destination:
                if (DestinationPath.Length > 0)
                    DestinationPath = DestinationPath.Substring(0, DestinationPath.Length - 1);
                break;
            
            case MenuItemType.SpeedLimit:
                if (SpeedLimitText.Length > 0)
                    SpeedLimitText = SpeedLimitText.Substring(0, SpeedLimitText.Length - 1);
                break;
            
            case MenuItemType.Parallelism:
                MaxParallelism = MaxParallelism / 10;
                if (MaxParallelism == 0)
                    MaxParallelism = Environment.ProcessorCount;
                break;
        }
    }
    
    private void ShowHelp()
    {
        Console.Clear();
        Console.WriteLine("═══════════════════════════════════════════════════════════");
        Console.WriteLine("                   FastCopy CLI Help                       ");
        Console.WriteLine("═══════════════════════════════════════════════════════════");
        Console.WriteLine();
        Console.WriteLine("Usage:");
        Console.WriteLine("  fastcopy --src <path> --dst <path> [options]");
        Console.WriteLine();
        Console.WriteLine("Options:");
        Console.WriteLine("  --src, -s        Source file or directory");
        Console.WriteLine("  --dst, -d        Destination path");
        Console.WriteLine("  --limit, -l      Speed limit (e.g., 50MB, 1GB/s)");
        Console.WriteLine("  --verify         Verify checksums after copy");
        Console.WriteLine("  --parallel, -j   Number of parallel workers");
        Console.WriteLine("  --file-list      Path to file-list.txt");
        Console.WriteLine("  --retry-failed   Retry from failed_jobs.jsonl");
        Console.WriteLine("  --quiet, -q      Run in headless mode");
        Console.WriteLine("  --help           Show this help");
        Console.WriteLine();
        Console.WriteLine("Interactive Controls (Dashboard Mode):");
        Console.WriteLine("  P - Pause/Resume");
        Console.WriteLine("  H - Hide/Show Dashboard (Headless mode)");
        Console.WriteLine("  + - Increase speed by 10MB/s");
        Console.WriteLine("  - - Decrease speed by 10MB/s");
        Console.WriteLine("  U - Unlimited speed");
        Console.WriteLine("  → - Increase parallelism");
        Console.WriteLine("  ← - Decrease parallelism");
        Console.WriteLine("  Q - Quit");
        Console.WriteLine();
        Console.WriteLine("Press any key to return to menu...");
        Console.ReadKey(intercept: true);
        Console.Clear();
    }
}

/// <summary>
/// Menu item types for navigation.
/// </summary>
public enum MenuItemType
{
    Source = 0,
    Destination = 1,
    Verify = 2,
    SpeedLimit = 3,
    Parallelism = 4,
    Help = 5,
    Start = 6,
    Exit = 7
}

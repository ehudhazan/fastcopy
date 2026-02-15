# FastCopy AOT-Safe TUI Dashboard

## Overview

This directory contains a **NativeAOT-compatible** Terminal User Interface (TUI) implementation that replaces Terminal.Gui with a custom, zero-allocation rendering system.

## Architecture

### Core Components

1. **ConsoleDashboard.cs** - Main dashboard controller
   - Zero-allocation rendering using `Span<char>` and `ISpanFormattable`
   - Background input handling (P=Pause, H=Hide, Esc=Exit, Arrow keys=Scroll)
   - ~10 FPS render loop to keep CPU usage under 1%
   
2. **ConsoleBuffer.cs** - Double-buffered console renderer
   - Dirty-rect tracking to only redraw changed cells
   - Prevents flickering by using `Console.SetCursorPosition`
   - Uses `ArrayPool<char>` for buffer management
   
3. **DashboardState.cs** - Immutable state structure
   - `readonly struct` for passing state to render method
   - Contains transfer items, progress, resource stats
   
4. **TransferItem.cs** - Represents a single file transfer
   - Simple data structure with FileName, Speed, Progress, Status

5. **DashboardDemo.cs** - Standalone demo program
   - Run with: `fastcopy --demo-dashboard`
   - Shows mock file transfers with simulated progress

## Zero-Allocation Principles

### Hot Path (Render Method)
- **NO** `string.Format()` or `$"{interpolation}"`
- **NO** `new` allocations
- **NO** boxing
- **YES** `Span<char>.TryWrite()`
- **YES** `ISpanFormattable`
- **YES** `stackalloc` (outside loops)

### Example: Zero-Allocation Text Formatting

```csharp
// ❌ BAD: Allocates strings
_buffer.WriteAt(10, 5, $"Progress: {progress:P1}");

// ✅ GOOD: Zero allocation
Span<char> buffer = stackalloc char[64];
if (buffer.TryWrite($"Progress: {progress:P1}", out int written))
{
    _buffer.WriteAt(10, 5, buffer.Slice(0, written));
}
```

## Integration Guide

### Step 1: Create Active Transfers Dictionary

```csharp
using System.Collections.Concurrent;
using FastCopy.Core;

var activeTransfers = new ConcurrentDictionary<string, ActiveTransfer>();
```

### Step 2: Create Pause Token

```csharp
var pauseTokenSource = new PauseTokenSource();
```

### Step 3: Create Dashboard

```csharp
using var dashboard = new ConsoleDashboard(
    activeTransfers,
    pauseTokenSource,
    resourceWatchdog: null); // Optional: pass ResourceWatchdog instance
```

### Step 4: Update Transfer Progress

In your copy workers, update the `activeTransfers` dictionary:

```csharp
foreach (var file in filesToCopy)
{
    var transfer = new ActiveTransfer
    {
        Source = file.SourcePath,
        Destination = file.DestPath,
        TotalBytes = file.Size,
        BytesTransferred = 0,
        BytesPerSecond = 0,
        Status = "Copying"
    };
    
    activeTransfers.TryAdd(file.SourcePath, transfer);
    
    // During copy...
    transfer.BytesTransferred += bytesRead;
    transfer.BytesPerSecond = currentSpeed;
    
    // When done...
    transfer.Status = "Completed";
}
```

### Step 5: Wait for Exit

```csharp
await dashboard.WaitForExitAsync();
```

### Full Example

See `CopyOperationHelper.cs` for integration with the actual copy engine.

## Keyboard Controls

| Key | Action |
|-----|--------|
| `P` | Pause/Resume all transfers |
| `H` | Hide/Show UI (headless mode) |
| `Esc` or `Q` | Exit dashboard |
| `↑` / `↓` | Scroll transfer list |
| `Page Up` / `Page Down` | Fast scroll |
| `Home` | Jump to top |

## Performance Characteristics

- **CPU Usage**: < 1% when idle, ~2-3% during active rendering
- **Memory**: ~50-100KB for console buffers (reused via ArrayPool)
- **Render Rate**: 10 FPS (configurable in `RenderLoop`)
- **Binary Size Impact**: ~50-100KB (no large UI framework)

## Differences from Terminal.Gui

| Feature | Terminal.Gui | ConsoleDashboard |
|---------|--------------|------------------|
| NativeAOT Support | ❌ No | ✅ Yes |
| Binary Size | +5-8 MB | +50-100 KB |
| Render Performance | Varies | Consistent 10 FPS |
| Memory Allocations | Many | Near-zero (hot path) |
| Cross-platform | ✅ Yes | ✅ Yes |
| Advanced Widgets | ✅ Yes | ❌ No (simple only) |

## Adding New Features

### To add a new display field:

1. Update `DashboardState` with new field:
   ```csharp
   public readonly string NewField;
   ```

2. Update `ConsoleDashboard.BuildDashboardState()` to populate it

3. Update `ConsoleDashboard.Render()` to display it using zero-allocation patterns

### To add a new keyboard command:

Update `InputLoop()` in `ConsoleDashboard.cs`:

```csharp
case ConsoleKey.N: // New key
    // Handle action
    break;
```

## Testing

### Unit Tests
```bash
dotnet test --filter "Category=Dashboard"
```

### Integration Test (Demo)
```bash
fastcopy --demo-dashboard
```

### AOT Compilation Test
```bash
dotnet publish -r linux-x64 -c Release -p:PublishAot=true
./publish/linux-x64/fastcopy --demo-dashboard
```

## Troubleshooting

### Flickering
- Ensure terminal supports ANSI escape codes
- Check that `ConsoleBuffer.Flush()` is using dirty-rect tracking

### High CPU Usage
- Increase delay in `RenderLoop()` (default: 100ms)
- Verify no tight loops in render methods

### Crashes on Resize
- Terminal resize is not currently handled
- Future: Add `Console.WindowWidth/Height` change detection

## Future Enhancements

- [ ] Terminal resize handling
- [ ] Mouse support (click to pause/resume individual transfers)
- [ ] Color themes
- [ ] Export progress to file
- [ ] Remote monitoring via gRPC

---

**NativeAOT Compliance**: ✅ All code in this directory is fully NativeAOT-compatible with no reflection or dynamic code generation.

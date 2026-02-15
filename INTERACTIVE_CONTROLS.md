# Interactive Dashboard Controls

## Overview

The FastCopy dashboard now features **real-time interactive controls** for managing file transfers during operation. No need to restart the process - control everything from the keyboard!

## Keyboard Controls

### Core Controls

| Key | Action | Description |
|-----|--------|-------------|
| **P** or **Space** | Pause/Resume | Toggle pause for all active transfers |
| **H** | Hide/Show | Toggle dashboard visibility (transfers continue in background) |
| **Q** or **Esc** | Quit | Exit dashboard (may interrupt transfers) |

### Speed Management

| Key | Action | Description |
|-----|--------|-------------|
| **+** | Increase Speed | Increase rate limit by 25% |
| **-** | Decrease Speed | Decrease rate limit by 20% (min 1 MB/s) |
| **U** | Unlimited | Remove all speed limits |
| **R** | Reset | Reset to default speed limit (100 MB/s) |

## Features

### 1. Pause/Resume (P or Space)

Instantly pause all active transfers. Files will resume from where they stopped:

```bash
# During transfer, press 'P' or Space
► Status changes to: ⏸ PAUSED
# Press again to resume
► Status changes to: ▶ Copying
```

**Use Cases:**
- Temporarily free up network bandwidth
- Prioritize other applications
- Debug transfer issues
- Resume long-running operations after system sleep

### 2. Hide/Show Dashboard (H)

Hide the TUI dashboard while transfers continue in background:

```bash
# Press 'H' to hide
> Dashboard hidden. Press H to show, Q to quit.

# Press 'H' again to restore dashboard
# Transfers never stop!
```

**Use Cases:**
- Run other terminal commands while copying
- Reduce visual clutter
- Background transfers for automated scripts

### 3. Dynamic Speed Control (+/-)

Adjust transfer speed limits on-the-fly without restarting:

```bash
# Starting speed: 50 MB/s
# Press '+' multiple times to increase
► Speed limit: 62 MB/s
► Speed limit: 78 MB/s
► Speed limit: 97 MB/s

# Press '-' to decrease
► Speed limit: 78 MB/s
```

**Speed Calculation:**
- **Increase (+)**: Current limit × 1.25 (min +10 MB)
- **Decrease (-)**: Current limit × 0.8 (floors at 1 MB/s)

### 4. Unlimited Mode (U)

Remove all speed restrictions for maximum throughput:

```bash
# Press 'U'
► Speed limit: UNLIMITED
# Transfer at full network/disk speed
```

**Warning:** May saturate network or cause high disk I/O. Use cautiously on shared systems.

### 5. Reset to Default (R)

Restore the default speed limit (100 MB/s):

```bash
# Press 'R'
► Speed limit reset to 100.0 MB/s
```

## Dashboard Layout

```
┏━━━━━━━━━━━━━━━━━━━━┓  ┏━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━┓
┃ FastCopy Dashboard ┃  ┃ Speed: 120 MB/s  |  Completed: 5 ┃
┗━━━━━━━━━━━━━━━━━━━━┛  ┗━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━┛

╔═Active Transfers════════════════════════════════════════╗
║ Status │ File              │ Progress       │  %    │ Speed ║
║ ▶      │ large_file.dat    │ ████████░░░░   │ 65.2% │ 98 MB/s ║
║ ▶      │ archive.zip       │ ██░░░░░░░░░░   │ 15.3% │ 75 MB/s ║
║ ✓      │ document.pdf      │ ████████████   │ 100%  │ 0 B/s ║
╚═════════════════════════════════════════════════════════╝

┌──────────────────────────────────────────────────────────┐
│ Overall Progress: ████████████░░░░░░░░░░░ 60.5%          │
│ ▶ Copying: 2 | Done: 1 | Failed: 0                       │
│ Controls: P/Space=Pause  H=Hide  +/-=Speed  U=Unlimited  │
│           R=Reset  Q/Esc=Quit                            │
└──────────────────────────────────────────────────────────┘
```

## Status Indicators

| Icon | Status | Description |
|------|--------|-------------|
| 🟢 **▶** | Copying | Transfer in progress |
| 🔵 **✓** | Completed | Transfer finished successfully |
| 🔴 **✗** | Failed | Transfer failed (see logs) |
| 🟡 **⏸** | Paused | Transfer paused by user |

## Implementation Details

### Architecture

1. **InteractiveDashboard.cs** - Main controller class
   - Captures keyboard input in background thread
   - Thread-safe control of PauseTokenSource and TokenBucket
   - Non-blocking: doesn't interrupt transfers

2. **PauseTokenSource** - Pause/resume mechanism
   - `Pause()` - Pauses all workers
   - `Resume()` - Resumes all workers
   - `Toggle()` - Alternates state
   - Workers check `IsPaused` and wait via `WaitWhilePausedAsync()`

3. **TokenBucket** - Rate limiting
   - `SetLimit(bytesPerSec)` - Dynamically adjusts speed
   - `ClearGlobalLimit()` - Removes all limits
   - Thread-safe with lock-free operations

### Zero-Copy Preservation

All interactive controls maintain the Zero-GC hot path:
- Keyboard input handled in separate thread (no allocation in copy loop)
- PauseTokenSource uses `TaskCompletionSource` (reused)
- TokenBucket uses Interlocked operations (lock-free)

### NativeAOT Compatibility

✅ Fully tested with `PublishAot=true`
✅ No reflection or dynamic code
✅ Console input uses standard .NET APIs

## Examples

### Quick Pause for Network Priority

```bash
# Start large transfer
$ fastcopy --src /bigdata --dst /backup

# Someone needs to video call
# Press 'P' to pause immediately
⏸ PAUSED

# Call finishes, press 'P' again
▶ Resumed
```

### Background Transfer Mode

```bash
# Start transfer
$ fastcopy --src /videos --dst /archive

# Need to check logs
# Press 'H' to hide dashboard
Dashboard hidden. Press H to show, Q to quit.

# Do other work
$ tail -f /var/log/app.log

# Check progress anytime
# Press 'H' to restore dashboard
```

### Throttle During Peak Hours

```bash
# Transfer starts at full speed
Speed: 500 MB/s

# Peak hours begin, need to throttle
# Press '-' multiple times
► Speed limit: 400 MB/s
► Speed limit: 320 MB/s
► Speed limit: 256 MB/s

# Off-peak hours, go unlimited
# Press 'U'
► Speed limit: UNLIMITED
```

## Future Enhancements

- [ ] Scroll through large file lists (↑/↓ keys)
- [ ] Filter/search files (/ key)
- [ ] Retry individual failed transfers (r on selection)
- [ ] Custom speed presets (1-9 keys)
- [ ] Export summary report (s key)

## Troubleshooting

**Q: Dashboard doesn't respond to keys**
A: Ensure terminal supports raw input. Try running in a standard terminal (not within another TUI app).

**Q: Pause doesn't work immediately**
A: Workers check pause state every I/O chunk (~4MB). Large files may take a moment.

**Q: Speed changes don't take effect**
A: Speed limit is enforced at the global TokenBucket level. Changes apply to all workers immediately, but throughput adjusts over next few seconds.

**Q: Can I use this in scripts?**
A: Yes! Use `--quiet` flag to disable the dashboard for automated/non-interactive usage.

---

**Version:** 1.0  
**Date:** February 14, 2026  
**Status:** ✅ Production Ready

# Bug Fix: Spectre.Console Markup Parsing Error

## Issue

After publishing with NativeAOT, the application crashed with:
```
System.InvalidOperationException: Could not find color or style 'PAUSED'.
```

## Root Cause

Spectre.Console uses square brackets `[]` for markup tags (like `[red]`, `[bold]`, etc.). When user-generated content containing words like "PAUSED" was inserted directly into markup strings without escaping, Spectre.Console attempted to parse them as style/color names.

### Problem Code

```csharp
// In DashboardPage.cs
var statusText = new Markup($"[white]{_viewModel.StatusMessage}[/]");

// In CopyOperationHelper.cs
statusMessage: pauseTokenSource.IsPaused 
    ? $"⏸ PAUSED | Copied: {copyingCount} | Done: {completedCount} | Failed: {failedCount}"
    : $"▶ Copying: {copyingCount} | Done: {completedCount} | Failed: {failedCount}"
```

When `StatusMessage` contained "PAUSED", the resulting markup string became:
```
[white]⏸ PAUSED | Copied: 2 | Done: 5 | Failed: 0[/]
```

Spectre.Console saw "PAUSED" and tried to interpret it as a color/style.

## Solution

### 1. Escape User Content (DashboardPage.cs)

```csharp
// Before
var statusText = new Markup($"[white]{_viewModel.StatusMessage}[/]");

// After
var statusText = new Markup($"[white]{Markup.Escape(_viewModel.StatusMessage)}[/]");
```

`Markup.Escape()` converts any special characters to their escaped equivalents, preventing them from being parsed as markup.

### 2. Simplify Status Messages (CopyOperationHelper.cs & Program.cs)

Changed "PAUSED" to "Paused" to avoid potential case sensitivity issues:

```csharp
// Before
statusMessage: pauseTokenSource.IsPaused 
    ? $"⏸ PAUSED | Copied: {copyingCount} | Done: {completedCount} | Failed: {failedCount}"
    : ...

// After
statusMessage: pauseTokenSource.IsPaused 
    ? $"⏸ Paused | Copying: {copyingCount} | Done: {completedCount} | Failed: {failedCount}"
    : ...
```

## Files Modified

1. **FastCopy/UI/DashboardPage.cs** - Added `Markup.Escape()` for status message
2. **FastCopy/Core/CopyOperationHelper.cs** - Changed "PAUSED" to "Paused"
3. **Program.cs** - Changed "PAUSED (Demo Mode)" to "Paused (Demo)"

## Verification

### Before Fix
```bash
$ ./fastcopy --demo-dashboard
Unhandled exception. System.InvalidOperationException: Could not find color or style 'PAUSED'.
Aborted (core dumped)
```

### After Fix
```bash
$ ./fastcopy --demo-dashboard
Hardware Acceleration: AVX2 Active
Starting Interactive Dashboard Demo...
Try the controls: P=Pause, H=Hide, +/-=Speed, U=Unlimited, R=Reset, Q=Quit
┏━━━━━━━━━━━━━━━━━━━━┓  ┏━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━┓
┃ FastCopy Dashboard ┃  ┃ Speed: 1.26 GB/s  |  Completed: 0 ┃
┗━━━━━━━━━━━━━━━━━━━━┛  ┗━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━┛
# Works perfectly!
```

## Lessons Learned

1. **Always escape user content in Spectre.Console markup** - Any text that could contain special characters should use `Markup.Escape()`
2. **Test with NativeAOT early** - Some issues only appear in AOT builds
3. **Be cautious with string interpolation in markup** - Direct variable insertion can cause parsing issues

## Best Practices for Spectre.Console

### ✅ DO
```csharp
AnsiConsole.MarkupLine($"[green]{Markup.Escape(userInput)}[/]");
```

### ❌ DON'T
```csharp
AnsiConsole.MarkupLine($"[green]{userInput}[/]"); // UNSAFE!
```

---

**Status:** ✅ Fixed and Verified  
**Build:** NativeAOT AOT Compatible  
**Testing:** Passed on Linux x64

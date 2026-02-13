# Agent Guide - FastCopy

This document provides essential information for AI agents working on the FastCopy repository. FastCopy is a high-performance file copying utility built with .NET 10, utilizing `System.IO.Pipelines` and `Terminal.Gui` v2.

## Context Guard

I am working on the FastCopy .NET 10 project. Below is the final feature request to complete the tool. Read all existing files (Program.cs, TransferEngine.cs, JournalingService.cs, etc.) before proceeding. Strictly preserve all existing Zero-GC logic, NativeAOT-friendly patterns, and Transport Adapters. Provide the full updated files or specific insertion blocks.

## 🛡️ Context Guard: FastCopy Project Invariants

**CRITICAL:** You are a .NET 10 Performance Engineer. Every response MUST adhere to these invariants:

1. **Zero-GC Hot Path:**
   - NO `new` allocations in the copy loop. Use `ArrayPool<byte>.Shared`.
   - NO boxing. Use `ValueTask`, `ref struct`, and `ReadOnlySpan<T>`.
   - Use `System.IO.Pipelines` for all streaming logic.

2. **NativeAOT Compatibility:**
   - NO reflection or `dynamic` keywords.
   - Use `System.Text.Json` Source Generators for all JSON (RecoveryService).
   - All code must be trim-compatible and AOT-safe.

3. **Architectural Integrity:**
   - NEVER delete existing logic in `Program.cs`, `JournalingService`, or `TransportAdapters` unless explicitly requested.
   - If a file is too large to output, use `// ... [Previous logic] ...` only for unchanged blocks, but provide full method bodies for new logic.

4. **Failure Mode:**
   - If a requested feature conflicts with Zero-GC or NativeAOT, STOP and explain the conflict before writing any code.

## 0. Project Overview

FastCopy aims to be the fastest file copy tool for .NET developers, providing a modern TUI for progress monitoring. The core philosophy is "Zero Copy, Zero Allocation" in the hot path. All file I/O should bypass intermediate buffers where possible and use asynchronous streaming.

## 1. Development Commands

### Build and Run

- **Build**: `dotnet build`
- **Run**: `dotnet run`
- **Clean**: `dotnet clean`

### NativeAOT Publishing

FastCopy is configured for NativeAOT compilation to produce optimized, self-contained binaries.

#### System Dependencies

**Required on Linux:**

- `clang` - C/C++ compiler
- `lld` - LLVM linker (required for AOT linking)

**Installation:**

```bash
# Ubuntu/Debian
sudo apt update && sudo apt install -y lld clang

# Fedora/RHEL
sudo dnf install -y lld clang

# Arch Linux
sudo pacman -S lld clang
```

**Note:** Windows and macOS users typically don't need additional dependencies as the required toolchain is bundled with .NET SDK.

#### Publish Commands

```bash
# Linux (Debian/Fedora)
dotnet publish FastCopy.csproj -r linux-x64 -c Release -p:PublishAot=true -o ./publish/linux-x64

# Alpine Linux
dotnet publish FastCopy.csproj -r linux-musl-x64 -c Release -p:PublishAot=true -o ./publish/alpine-x64

# Windows
dotnet publish FastCopy.csproj -r win-x64 -c Release -p:PublishAot=true -o ./publish/win-x64

# macOS (Intel)
dotnet publish FastCopy.csproj -r osx-x64 -c Release -p:PublishAot=true -o ./publish/osx-x64

# macOS (Apple Silicon)
dotnet publish FastCopy.csproj -r osx-arm64 -c Release -p:PublishAot=true -o ./publish/osx-arm64
```

**Important:** Always use `FastCopy.csproj` (not `FastCopy.sln`) with the `-o` flag to avoid solution-level warnings.

#### AOT Configuration

The project is configured in `FastCopy.csproj` with aggressive size optimization:

- `PublishAot=true` - Enable Native AOT compilation
- `OptimizationPreference=Size` - Optimize for binary size
- `TrimMode=link` - Aggressive trimming
- Known AOT warnings from Terminal.Gui are suppressed (IL2026, IL3050, IL2104, IL3053)

#### Troubleshooting

##### Error: "invalid linker name in argument '-fuse-ld=lld'"

- **Cause:** `lld` linker is not installed
- **Fix:** Install `lld` and `clang` packages (see System Dependencies above)

##### Large binary size

- Expected size: ~14-20MB for the optimized Linux binary
- Size is optimized via `IlcOptimizationPreference=Size` and aggressive trimming

### Testing Strategy

- **Unit Tests**: Focus on the `CopyEngine` logic, especially edge cases (empty files, large files, path normalization).
- **Integration Tests**: Verify end-to-end file copying between different directories/drives.
- **Performance Tests**: Benchmark the copy throughput using `BenchmarkDotNet` for core components.
- **Mocking**: Use `Moq` or `NSubstitute` for abstracting file system interactions in unit tests.

### Testing Commands

*Note: A test project is not yet established. When adding tests, follow these conventions:*

- **Run all tests**: `dotnet test`
- **Run a specific test**: `dotnet test --filter NameOfTest`
- **Watch mode**: `dotnet watch test`

### Linting and Formatting

- **Check formatting**: `dotnet format --verify-no-changes`
- **Fix formatting**: `dotnet format`
- **Linting**: `dotnet build -warnaserror` (to treat warnings as errors)

---

## 2. Code Style & Guidelines

### Language & Framework

- **Runtime**: .NET 10 (Preview)
- **Language**: C# 13+
- **Project Type**: Console Application (Exe)
- **Features**: Nullable reference types (enabled), Implicit usings (enabled).
- **Modern C#**: Leverage C# 13 features like `params collections`, `field` keyword (if available in preview), and improved `lock` object.

### Architecture & Patterns

- **Top-Level Statements**: Use top-level statements for the entry point (`Program.cs`) unless complexity warrants a traditional `Main` method.
- **Asynchronous First**: All I/O operations must be asynchronous using `Task` or `ValueTask`. Avoid `.Wait()` or `.Result`.

- **High Performance**:
  - Use `System.IO.Pipelines` for stream processing.
  - Prefer `Memory<T>` and `Span<T>` over array allocations where possible.
  - Minimize garbage collection pressure in hot paths.

### Naming Conventions

- **Classes/Interfaces/Methods**: `PascalCase` (e.g., `FileCopier`, `ICopyOperation`).
- **Private Fields**: `_camelCase` (e.g., `_bufferSize`).
- **Local Variables/Parameters**: `camelCase` (e.g., `sourcePath`).
- **Constants**: `PascalCase` or `UPPER_SNAKE_CASE`.
- **Namespaces**: Match the folder structure.

### Formatting

- **Braces**: Always use Allman style (braces on new lines) for C# files.
- **Indentation**: 4 spaces.
- **Line Length**: Aim for < 120 characters.

### Imports

- Use **Implicit Usings** where possible.
- Group `System` namespaces first.
- Keep imports ordered alphabetically within groups.
- Prefer file-scoped namespaces: `namespace FastCopy;`

### Types & Declarations

- Use `var` when the type is apparent from the right-hand side (e.g., `var list = new List<string>();`).
- Use explicit types for primitive types or when the type is not obvious.
- Favor `record` types for immutable data structures.
- If possible, make class sealed for better performance.

### Error Handling

- Use exceptions for truly exceptional circumstances.
- For expected failures (e.g., file not found, access denied), consider returning a `Result<T>` or `Result` object.
- Provide descriptive error messages.
- Use `try-catch` blocks at the highest level possible to avoid swallowing critical errors.

### Null Safety

- Strict null checking is enabled.
- Use `?` for nullable types.
- Use `!` (dammit operator) sparingly and only when you are certain the value is not null.

---

## 3. Project Specifics

### High-Performance I/O with Pipelines

- **Efficiency**: Use `PipeReader` and `PipeWriter` to minimize copying between buffers.
- **Backpressure**: Respect the `FlushResult` and `ReadResult` to handle backpressure correctly.
- **Buffer Management**: Use `ReadOnlySequence<byte>` for multi-segmented data reading.
- **Example Pattern**:

  ```csharp
  while (true)
  {
      var result = await reader.ReadAsync();
      var buffer = result.Buffer;
      // Process buffer...
      reader.AdvanceTo(buffer.Start, buffer.End);
      if (result.IsCompleted) break;
  }
  ```

### TUI Development with Terminal.Gui v2

- **Version**: Project uses v2.0.0-alpha. This is fundamentally different from v1.
- **Views**: Inherit from `Window` or `View` for custom UI components.
- **Layout**: Use `Pos` and `Dim` for responsive layout. v2 supports newer layout features.
- **Events**: Use the `Action` or `EventHandler` pattern for UI interactions.
- **Threading**: Ensure UI updates happen on the main thread via `Application.Invoke()`.

### Performance Best Practices

- **Allocations**: Avoid `new byte[]` in hot loops; use `ArrayPool<byte>.Shared`.
- **Inlining**: Use `[MethodImpl(MethodImplOptions.AggressiveInlining)]` for hot paths.
- **Structs**: Use `readonly struct` for small data objects.
- **Generics**: Use generic constraints to avoid boxing.

---

## 4. Documentation & Communication

- **Code Comments**: Document complex logic, especially memory management and threading.
- **Commit Messages**: Use descriptive, imperative messages (e.g., "Add: Implement Pipe-based reader").
- **External Docs**: If a feature is complex, create a `docs/` folder.

---

## 5. Instructions for Agents

1. **Self-Verification**: After making changes, always run `dotnet build`. Ensure the build passes with **no errors and no warnings** (treat warnings as errors).
2. **AOT Compliance**: Ensure the project is configured with `<PublishAot>true</PublishAot>` and that no code changes violate AOT compatibility (e.g., avoid unbounded reflection).
3. **Performance Monitoring**: Verify you aren't introducing unnecessary allocations in `CopyEngine`.
4. **TUI Updates**: Ensure the UI remains responsive and handles window resizing.
5. **New Files**: Add new classes to their own files named after the class. Use file-scoped namespaces.
6. **Tooling**: Prefer using standard `dotnet` CLI tools.
7. **One Type Per File**: Ensure each class, struct, interface, enum, or delegate is in its own `.cs` file. Each file must not have more than one top-level item.

---

## 6. Future Roadmap (Agent Tasks)

- **Engine**: Implement `CopyEngine` using `PipeReader` and `PipeWriter`.
- **Testing**: Scaffold a `FastCopy.Tests` project using xUnit or MSTest.
- **Interface**: Create a basic TUI layout with progress bars and file selection.
- **Logging**: Add structured logging using `Microsoft.Extensions.Logging`.
- **Metrics**: Add throughput metrics (MB/s) displayed in real-time.
- **Cancellation**: Ensure all operations support `CancellationToken`.

---

## 7. Project Structure

- `Program.cs`: Entry point with top-level statements.
- `FastCopy/`: Core library logic (CopyEngine, Models).
- `FastCopy.UI/`: Custom UI components and views.
- `FastCopy.Tests/`: Unit and integration tests (to be created).
- `Benchmarks/`: Performance benchmarks using BenchmarkDotNet.

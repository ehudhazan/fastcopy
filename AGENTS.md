# Agent Guide - FastCopy

This document provides essential information for AI agents working on the FastCopy repository. FastCopy is a high-performance file copying utility built with .NET 10, utilizing `System.IO.Pipelines` and `Terminal.Gui` v2.

## 0. Project Overview
FastCopy aims to be the fastest file copy tool for .NET developers, providing a modern TUI for progress monitoring. The core philosophy is "Zero Copy, Zero Allocation" in the hot path. All file I/O should bypass intermediate buffers where possible and use asynchronous streaming.

## 1. Development Commands

### Build and Run
- **Build**: `dotnet build`
- **Run**: `dotnet run`
- **Clean**: `dotnet clean`

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
1. **Self-Verification**: After making changes, always run `dotnet build`.
2. **Performance Monitoring**: Verify you aren't introducing unnecessary allocations in `CopyEngine`.
3. **TUI Updates**: Ensure the UI remains responsive and handles window resizing.
4. **New Files**: Add new classes to their own files named after the class. Use file-scoped namespaces.
5. **Tooling**: Prefer using standard `dotnet` CLI tools.

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

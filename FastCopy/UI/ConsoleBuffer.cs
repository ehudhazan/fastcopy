using System.Buffers;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace FastCopy.UI;

/// <summary>
/// Zero-allocation console buffer with dirty-rect tracking to prevent flickering.
/// Uses double-buffering and only redraws changed cells.
/// NativeAOT-compatible.
/// </summary>
public sealed class ConsoleBuffer : IDisposable
{
    private readonly int _width;
    private readonly int _height;
    private readonly char[] _currentBuffer;
    private readonly char[] _previousBuffer;
    private readonly ConsoleColor[] _currentColorBuffer;
    private readonly ConsoleColor[] _previousColorBuffer;
    private bool _disposed;

    public int Width => _width;
    public int Height => _height;

    public ConsoleBuffer(int width, int height)
    {
        _width = width;
        _height = height;
        
        int size = width * height;
        _currentBuffer = ArrayPool<char>.Shared.Rent(size);
        _previousBuffer = ArrayPool<char>.Shared.Rent(size);
        _currentColorBuffer = ArrayPool<ConsoleColor>.Shared.Rent(size);
        _previousColorBuffer = ArrayPool<ConsoleColor>.Shared.Rent(size);
        
        // Initialize with spaces
        Array.Fill(_currentBuffer, ' ', 0, size);
        Array.Fill(_previousBuffer, ' ', 0, size);
        Array.Fill(_currentColorBuffer, ConsoleColor.Gray, 0, size);
        Array.Fill(_previousColorBuffer, ConsoleColor.Gray, 0, size);
    }

    /// <summary>
    /// Clear the buffer with spaces.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Clear()
    {
        int size = _width * _height;
        Array.Fill(_currentBuffer, ' ', 0, size);
        Array.Fill(_currentColorBuffer, ConsoleColor.Gray, 0, size);
    }

    /// <summary>
    /// Write text at the specified position using zero-allocation span-based writing.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void WriteAt(int x, int y, ReadOnlySpan<char> text, ConsoleColor color = ConsoleColor.Gray)
    {
        if (y < 0 || y >= _height || x < 0 || x >= _width)
            return;

        int startIdx = y * _width + x;
        int remaining = _width - x;
        int length = Math.Min(text.Length, remaining);

        if (length <= 0)
            return;

        text.Slice(0, length).CopyTo(_currentBuffer.AsSpan(startIdx, length));
        _currentColorBuffer.AsSpan(startIdx, length).Fill(color);
    }

    /// <summary>
    /// Write formatted text using ISpanFormattable to avoid allocations.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryWriteFormatted<T>(int x, int y, T value, ReadOnlySpan<char> format, ConsoleColor color = ConsoleColor.Gray)
        where T : ISpanFormattable
    {
        if (y < 0 || y >= _height || x < 0 || x >= _width)
            return false;

        Span<char> tempBuffer = stackalloc char[128];
        
        if (value.TryFormat(tempBuffer, out int charsWritten, format, null))
        {
            WriteAt(x, y, tempBuffer.Slice(0, charsWritten), color);
            return true;
        }

        return false;
    }

    /// <summary>
    /// Draw a horizontal line.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void DrawHorizontalLine(int x, int y, int length, char lineChar = '─', ConsoleColor color = ConsoleColor.Gray)
    {
        if (y < 0 || y >= _height || x < 0 || x >= _width)
            return;

        int startIdx = y * _width + x;
        int remaining = _width - x;
        int actualLength = Math.Min(length, remaining);

        if (actualLength <= 0)
            return;

        _currentBuffer.AsSpan(startIdx, actualLength).Fill(lineChar);
        _currentColorBuffer.AsSpan(startIdx, actualLength).Fill(color);
    }

    /// <summary>
    /// Draw a vertical line.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void DrawVerticalLine(int x, int y, int length, char lineChar = '│', ConsoleColor color = ConsoleColor.Gray)
    {
        if (x < 0 || x >= _width || y < 0 || y >= _height)
            return;

        int maxLength = Math.Min(length, _height - y);

        for (int i = 0; i < maxLength; i++)
        {
            int idx = (y + i) * _width + x;
            _currentBuffer[idx] = lineChar;
            _currentColorBuffer[idx] = color;
        }
    }

    /// <summary>
    /// Draw a box with borders.
    /// </summary>
    public void DrawBox(int x, int y, int width, int height, ConsoleColor color = ConsoleColor.Gray)
    {
        if (width < 2 || height < 2)
            return;

        // Top border
        WriteAt(x, y, "┌", color);
        DrawHorizontalLine(x + 1, y, width - 2, '─', color);
        WriteAt(x + width - 1, y, "┐", color);

        // Bottom border
        WriteAt(x, y + height - 1, "└", color);
        DrawHorizontalLine(x + 1, y + height - 1, width - 2, '─', color);
        WriteAt(x + width - 1, y + height - 1, "┘", color);

        // Side borders
        for (int i = 1; i < height - 1; i++)
        {
            WriteAt(x, y + i, "│", color);
            WriteAt(x + width - 1, y + i, "│", color);
        }
    }

    /// <summary>
    /// Flush the buffer to the console, only updating changed cells.
    /// This is where the "dirty-rect" optimization happens.
    /// </summary>
    public void Flush()
    {
        int size = _width * _height;
        ConsoleColor currentColor = Console.ForegroundColor;
        
        for (int i = 0; i < size; i++)
        {
            // Only update cells that have changed
            if (_currentBuffer[i] != _previousBuffer[i] || _currentColorBuffer[i] != _previousColorBuffer[i])
            {
                int x = i % _width;
                int y = i / _width;

                // Set cursor position
                Console.SetCursorPosition(x, y);

                // Set color if changed
                if (_currentColorBuffer[i] != currentColor)
                {
                    currentColor = _currentColorBuffer[i];
                    Console.ForegroundColor = currentColor;
                }

                // Write character
                Console.Write(_currentBuffer[i]);

                // Update previous buffer
                _previousBuffer[i] = _currentBuffer[i];
                _previousColorBuffer[i] = _currentColorBuffer[i];
            }
        }

        // Reset cursor to bottom
        Console.SetCursorPosition(0, _height - 1);
        Console.ForegroundColor = ConsoleColor.Gray;
    }

    /// <summary>
    /// Force a full redraw of the entire buffer.
    /// </summary>
    public void ForceRedraw()
    {
        Console.Clear();
        Array.Fill(_previousBuffer, '\0');
        Array.Fill(_previousColorBuffer, (ConsoleColor)255);
        Flush();
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;

        ArrayPool<char>.Shared.Return(_currentBuffer);
        ArrayPool<char>.Shared.Return(_previousBuffer);
        ArrayPool<ConsoleColor>.Shared.Return(_currentColorBuffer);
        ArrayPool<ConsoleColor>.Shared.Return(_previousColorBuffer);
    }
}

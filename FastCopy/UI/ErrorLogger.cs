using System.Runtime.CompilerServices;
using System.Text;

namespace FastCopy.UI;

/// <summary>
/// Simple, async error logger for crash-free input handling.
/// AOT-safe, thread-safe, minimal allocations.
/// </summary>
public sealed class ErrorLogger
{
    private readonly string _logFilePath;
    private readonly SemaphoreSlim _writeLock;
    
    public ErrorLogger(string logFilePath)
    {
        _logFilePath = logFilePath;
        _writeLock = new SemaphoreSlim(1, 1);
    }
    
    /// <summary>
    /// Log an error message asynchronously without throwing exceptions.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public async Task LogAsync(string message)
    {
        try
        {
            await _writeLock.WaitAsync();
            try
            {
                string timestamp = DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss.fff");
                string logEntry = $"[{timestamp}] {message}\n";
                
                await File.AppendAllTextAsync(_logFilePath, logEntry);
            }
            finally
            {
                _writeLock.Release();
            }
        }
        catch
        {
            // Swallow all exceptions - logging should never crash the app
        }
    }
    
    /// <summary>
    /// Synchronous logging for use in finalizers or critical paths.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Log(string message)
    {
        try
        {
            _writeLock.Wait();
            try
            {
                string timestamp = DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss.fff");
                string logEntry = $"[{timestamp}] {message}\n";
                
                File.AppendAllText(_logFilePath, logEntry);
            }
            finally
            {
                _writeLock.Release();
            }
        }
        catch
        {
            // Swallow all exceptions
        }
    }
}

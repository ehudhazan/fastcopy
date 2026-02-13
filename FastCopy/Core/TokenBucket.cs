using System.Diagnostics;

namespace FastCopy.Core;

internal sealed class TokenBucket
{
    private readonly long _maxTokens;
    private readonly long _refillRate; // Tokens per second
    private double _tokens;
    private long _lastRefillTick;
    private readonly object _lock = new();

    public TokenBucket(long bytesPerSecond)
    {
        if (bytesPerSecond <= 0) throw new ArgumentOutOfRangeException(nameof(bytesPerSecond));
        _maxTokens = bytesPerSecond; 
        _refillRate = bytesPerSecond;
        _tokens = _maxTokens;
        _lastRefillTick = Stopwatch.GetTimestamp();
    }

    public async ValueTask ConsumeAsync(long count, CancellationToken cancellationToken)
    {
        while (count > 0)
        {
            long waitMs = 0;
            
            lock (_lock)
            {
                Refill();

                if (_tokens >= count)
                {
                    _tokens -= count;
                    return;
                }

                // Not enough tokens, calculate wait
                double deficit = count - _tokens;
                double secondsToWait = deficit / _refillRate;
                waitMs = (long)(secondsToWait * 1000);
                
                // If wait is too long (e.g. > 100ms), we can just take what's available and wait for the rest in next iteration?
                // But simplified: just wait.
                // However, if we wait, we must not consume tokens yet.
                // Actually, standard token bucket consumes what's available or waits.
                // To minimize wakeups, we wait until we have enough for the whole chunk? 
                // Or at least a reasonable chunk.
                // Given standard segments are 4KB, waiting for 4KB at 1MB/s is 4ms.
                
                if (waitMs == 0) waitMs = 1; // Minimum wait
            }

            await Task.Delay((int)waitMs, cancellationToken);
        }
    }

    private void Refill()
    {
        long now = Stopwatch.GetTimestamp();
        double elapsedSeconds = (double)(now - _lastRefillTick) / Stopwatch.Frequency;
        
        if (elapsedSeconds > 0)
        {
            double newTokens = elapsedSeconds * _refillRate;
            _tokens = Math.Min(_maxTokens, _tokens + newTokens);
            _lastRefillTick = now;
        }
    }
}

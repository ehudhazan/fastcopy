using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace FastCopy.Core;

/// <summary>
/// Global rate limiter using token bucket algorithm with Zero-GC design.
/// Thread-safe using Interlocked operations only. Supports dynamic limit changes.
/// </summary>
public sealed class TokenBucket
{
    // Scale factor for fractional token precision (tokens stored as fixed-point: actualTokens * SCALE)
    private const long SCALE = 1000;
    
    // Bypass mode: if limit is 0, completely skip rate limiting
    private long _bypassMode; // 0 = rate limit, 1 = bypass
    
    // Token state (scaled by SCALE for precision)
    private long _scaledTokens;
    private long _scaledMaxTokens;
    private long _scaledRefillRate; // Scaled tokens per second
    
    // Timing for refills
    private long _lastRefillTick;
    
    // For SpinWait calibration
    private static readonly long TicksPerMicrosecond = Stopwatch.Frequency / 1_000_000;

    public TokenBucket(long bytesPerSecond)
    {
        if (bytesPerSecond < 0) throw new ArgumentOutOfRangeException(nameof(bytesPerSecond));
        
        if (bytesPerSecond == 0)
        {
            // Bypass mode - no rate limiting
            _bypassMode = 1;
            _scaledMaxTokens = 0;
            _scaledRefillRate = 0;
            _scaledTokens = 0;
        }
        else
        {
            _bypassMode = 0;
            _scaledMaxTokens = bytesPerSecond * SCALE;
            _scaledRefillRate = bytesPerSecond * SCALE;
            _scaledTokens = _scaledMaxTokens; // Start with full bucket
        }
        
        _lastRefillTick = Stopwatch.GetTimestamp();
    }

    /// <summary>
    /// Change the rate limit dynamically while the application is running.
    /// </summary>
    /// <param name="newBytesPerSecond">New limit in bytes/second. 0 = unlimited (bypass mode).</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void ChangeLimit(long newBytesPerSecond)
    {
        if (newBytesPerSecond < 0) throw new ArgumentOutOfRangeException(nameof(newBytesPerSecond));
        
        if (newBytesPerSecond == 0)
        {
            // Enable bypass mode
            Interlocked.Exchange(ref _bypassMode, 1);
            Interlocked.Exchange(ref _scaledMaxTokens, 0);
            Interlocked.Exchange(ref _scaledRefillRate, 0);
        }
        else
        {
            // Disable bypass mode and set new limits
            long newScaledMax = newBytesPerSecond * SCALE;
            long newScaledRate = newBytesPerSecond * SCALE;
            
            Interlocked.Exchange(ref _scaledMaxTokens, newScaledMax);
            Interlocked.Exchange(ref _scaledRefillRate, newScaledRate);
            
            // Cap current tokens to new max if necessary
            long currentTokens = Interlocked.Read(ref _scaledTokens);
            if (currentTokens > newScaledMax)
            {
                Interlocked.Exchange(ref _scaledTokens, newScaledMax);
            }
            
            // Ensure bypass is disabled last
            Interlocked.Exchange(ref _bypassMode, 0);
        }
    }

    /// <summary>
    /// Set a new rate limit (alias for ChangeLimit).
    /// Supports live updates while transfers are in progress.
    /// </summary>
    /// <param name="bytesPerSec">New limit in bytes/second. 0 = unlimited (bypass mode).</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void SetLimit(long bytesPerSec)
    {
        ChangeLimit(bytesPerSec);
    }

    /// <summary>
    /// Attempt to consume tokens from the bucket. Blocks using SpinWait if insufficient tokens.
    /// Zero-GC design: uses Interlocked operations and SpinWait instead of locks or Task.Delay.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Consume(long bytes, CancellationToken cancellationToken)
    {
        // Fast path: bypass mode
        if (Interlocked.Read(ref _bypassMode) == 1)
        {
            return;
        }

        long scaledBytes = bytes * SCALE;
        var spinner = new SpinWait();
        
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            
            // Refill tokens based on elapsed time
            Refill();
            
            // Try to consume tokens
            long currentTokens = Interlocked.Read(ref _scaledTokens);
            
            if (currentTokens >= scaledBytes)
            {
                // Attempt to atomically subtract tokens
                long newTokens = Interlocked.Add(ref _scaledTokens, -scaledBytes);
                
                // Check if we went negative (another thread consumed first)
                if (newTokens >= 0)
                {
                    // Success!
                    return;
                }
                else
                {
                    // We went negative, add back and retry
                    Interlocked.Add(ref _scaledTokens, scaledBytes);
                }
            }
            
            // Not enough tokens, spin wait
            // For large deficits, yield to other threads more aggressively
            if (currentTokens < scaledBytes / 2)
            {
                spinner.SpinOnce(sleep1Threshold: 10);
            }
            else
            {
                spinner.SpinOnce(sleep1Threshold: 50);
            }
        }
    }

    /// <summary>
    /// Refill the token bucket based on elapsed time since last refill.
    /// Uses Interlocked operations for thread-safe, lock-free refills.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void Refill()
    {
        long now = Stopwatch.GetTimestamp();
        long lastTick = Interlocked.Read(ref _lastRefillTick);
        
        // Calculate elapsed time
        long elapsedTicks = now - lastTick;
        
        if (elapsedTicks <= 0)
        {
            return;
        }
        
        // Try to claim this refill period
        if (Interlocked.CompareExchange(ref _lastRefillTick, now, lastTick) != lastTick)
        {
            // Another thread already updated, skip this refill
            return;
        }
        
        // Calculate tokens to add (scaled)
        long refillRate = Interlocked.Read(ref _scaledRefillRate);
        long tokensToAdd = (elapsedTicks * refillRate) / Stopwatch.Frequency;
        
        if (tokensToAdd <= 0)
        {
            return;
        }
        
        // Add tokens, but cap at max
        long maxTokens = Interlocked.Read(ref _scaledMaxTokens);
        
        while (true)
        {
            long currentTokens = Interlocked.Read(ref _scaledTokens);
            long newTokens = Math.Min(maxTokens, currentTokens + tokensToAdd);
            
            if (newTokens == currentTokens)
            {
                // Already at max
                break;
            }
            
            if (Interlocked.CompareExchange(ref _scaledTokens, newTokens, currentTokens) == currentTokens)
            {
                // Successfully updated
                break;
            }
            
            // Another thread modified, retry
        }
    }
}

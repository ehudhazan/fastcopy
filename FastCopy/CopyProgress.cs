namespace FastCopy;

/// <summary>
/// Represents progress information using a ref struct to avoid heap allocation.
/// Note: This cannot be used across async awaits, but is useful for synchronous reporting/calculations.
/// </summary>
public ref struct CopyProgress
{
    public long TotalBytesCopied;
    public long BytesPerSecond; // Placeholder for potential speed calculation
}

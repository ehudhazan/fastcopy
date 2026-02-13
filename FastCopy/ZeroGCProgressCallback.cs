namespace FastCopy;

/// <summary>
/// Delegate for reporting progress without boxing.
/// </summary>
public delegate void ZeroGCProgressCallback(ref CopyProgress progress);

namespace FastCopy.Transports;

/// <summary>
/// Defines a transport adapter for copying streams to destinations.
/// Implementations handle different protocols (local, SSH, Docker, K8s, etc.).
/// </summary>
public interface ITransportAdapter
{
    /// <summary>
    /// Copies data from a source stream to a destination path.
    /// </summary>
    /// <param name="source">The source stream to copy from.</param>
    /// <param name="destinationPath">The destination path (local or remote).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A ValueTask representing the asynchronous operation.</returns>
    ValueTask CopyStreamAsync(Stream source, string destinationPath, CancellationToken ct);
}

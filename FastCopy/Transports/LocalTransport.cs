namespace FastCopy.Transports;

/// <summary>
/// Local file system transport adapter using ZeroGCProvider for high-performance copying.
/// </summary>
public sealed class LocalTransport : ITransportAdapter
{
    private readonly ZeroGCProvider _provider;

    public LocalTransport(long maxBytesPerSecond = 0)
    {
        _provider = new ZeroGCProvider(maxBytesPerSecond);
    }

    /// <inheritdoc />
    public async ValueTask CopyStreamAsync(Stream source, string destinationPath, CancellationToken ct)
    {
        // Ensure the destination directory exists
        var directory = Path.GetDirectoryName(destinationPath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        // Open destination file with async I/O and write-through for reliability
        await using var destination = new FileStream(
            destinationPath,
            FileMode.Create,
            FileAccess.Write,
            FileShare.None,
            bufferSize: 1, // Minimal buffer since pipelines handle buffering
            useAsync: true);

        // Use ZeroGCProvider for efficient stream copying
        await _provider.CopyStreamAsync(source, destination, onProgress: null, cancellationToken: ct);
    }
}

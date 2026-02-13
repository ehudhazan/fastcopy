namespace FastCopy.Transports;

/// <summary>
/// Factory for creating transport adapters based on URI schemes.
/// </summary>
public static class TransportFactory
{
    /// <summary>
    /// Creates a transport adapter based on the URI scheme.
    /// </summary>
    /// <param name="destinationUri">The destination URI or path.</param>
    /// <param name="maxBytesPerSecond">Optional bandwidth limit for the transport.</param>
    /// <param name="sshPrivateKeyPath">Optional path to SSH private key for SSH/SFTP transport.</param>
    /// <param name="sshPrivateKeyPassphrase">Optional passphrase for the SSH private key.</param>
    /// <returns>An appropriate ITransportAdapter implementation.</returns>
    /// <exception cref="NotSupportedException">Thrown when the URI scheme is not supported.</exception>
    public static ITransportAdapter CreateTransport(
        string destinationUri, 
        long maxBytesPerSecond = 0,
        string? sshPrivateKeyPath = null,
        string? sshPrivateKeyPassphrase = null)
    {
        if (string.IsNullOrWhiteSpace(destinationUri))
        {
            throw new ArgumentException("Destination URI cannot be null or empty.", nameof(destinationUri));
        }

        // Try to parse as URI
        if (Uri.TryCreate(destinationUri, UriKind.Absolute, out var uri))
        {
            return uri.Scheme.ToLowerInvariant() switch
            {
                "file" => new LocalTransport(maxBytesPerSecond),
                "ssh" or "sftp" => new SshTransport(maxBytesPerSecond, sshPrivateKeyPath, sshPrivateKeyPassphrase),
                "docker" => new DockerTransport(maxBytesPerSecond),
                "k8s" => new K8sTransport(maxBytesPerSecond),
                _ => throw new NotSupportedException($"URI scheme '{uri.Scheme}' is not supported.")
            };
        }

        // Treat as local file path if not a valid URI
        return new LocalTransport(maxBytesPerSecond);
    }

    /// <summary>
    /// Determines if a given URI scheme is supported.
    /// </summary>
    /// <param name="destinationUri">The destination URI or path to check.</param>
    /// <returns>True if the scheme is supported; otherwise, false.</returns>
    public static bool IsSupported(string destinationUri)
    {
        if (string.IsNullOrWhiteSpace(destinationUri))
        {
            return false;
        }

        if (Uri.TryCreate(destinationUri, UriKind.Absolute, out var uri))
        {
            return uri.Scheme.ToLowerInvariant() is "file" or "ssh" or "sftp" or "docker" or "k8s";
        }

        // Local paths are always supported
        return true;
    }
}

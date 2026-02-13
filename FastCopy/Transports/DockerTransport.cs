using System.Buffers;
using System.Text;
using Docker.DotNet;
using Docker.DotNet.Models;

namespace FastCopy.Transports;

/// <summary>
/// Docker transport adapter that copies files into containers using tar streaming.
/// Uses the 'Stream Tar' approach (equivalent to docker cp -) to send files to containers
/// without requiring tar or fastcopy to be installed in the container.
/// </summary>
public sealed class DockerTransport : ITransportAdapter, IDisposable
{
    private readonly DockerClient _client;
    private readonly long _maxBytesPerSecond;
    private bool _disposed;

    /// <summary>
    /// Creates a new DockerTransport instance.
    /// </summary>
    /// <param name="maxBytesPerSecond">Optional bandwidth limit for the transport.</param>
    /// <param name="dockerEndpoint">Optional Docker endpoint URI. Defaults to local Docker socket.</param>
    public DockerTransport(long maxBytesPerSecond = 0, Uri? dockerEndpoint = null)
    {
        _maxBytesPerSecond = maxBytesPerSecond;
        
        // Use default Docker endpoint if none provided
        dockerEndpoint ??= GetDefaultDockerEndpoint();
        
        _client = new DockerClientConfiguration(dockerEndpoint).CreateClient();
    }

    /// <inheritdoc />
    public async ValueTask CopyStreamAsync(Stream source, string destinationPath, CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        // Parse the docker:// URI to extract container ID and path
        // Expected format: docker://container_id/path/to/file
        var (containerId, containerPath) = ParseDockerUri(destinationPath);

        // Get file info for tar header
        long fileSize = source.CanSeek ? source.Length : 0;
        
        // Create tar stream on the fly and send to container
        await using var tarStream = CreateTarStream(source, containerPath, fileSize);
        
        // Extract path components for Docker API
        string? directory = Path.GetDirectoryName(containerPath);
        string targetPath = string.IsNullOrEmpty(directory) ? "/" : directory;

        // Send tar stream to container
        var parameters = new ContainerPathStatParameters
        {
            AllowOverwriteDirWithFile = true,
            Path = targetPath
        };

        await _client.Containers.ExtractArchiveToContainerAsync(
            containerId,
            parameters,
            tarStream,
            ct);
    }

    /// <summary>
    /// Creates a tar stream on the fly from the source stream.
    /// </summary>
    private Stream CreateTarStream(Stream source, string fileName, long fileSize)
    {
        var tarStream = new TarStream(source, fileName, fileSize, _maxBytesPerSecond);
        return tarStream;
    }

    /// <summary>
    /// Parses a docker:// URI into container ID and path components.
    /// </summary>
    /// <param name="destinationPath">URI in format docker://container_id/path/to/file</param>
    /// <returns>Tuple of (containerId, containerPath)</returns>
    private static (string containerId, string containerPath) ParseDockerUri(string destinationPath)
    {
        if (!destinationPath.StartsWith("docker://", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("Destination path must start with docker://", nameof(destinationPath));
        }

        string pathPart = destinationPath["docker://".Length..];
        int slashIndex = pathPart.IndexOf('/');

        if (slashIndex <= 0)
        {
            throw new ArgumentException(
                "Invalid docker URI format. Expected: docker://container_id/path/to/file", 
                nameof(destinationPath));
        }

        string containerId = pathPart[..slashIndex];
        string containerPath = pathPart[slashIndex..];

        if (string.IsNullOrWhiteSpace(containerId))
        {
            throw new ArgumentException("Container ID cannot be empty", nameof(destinationPath));
        }

        return (containerId, containerPath);
    }

    /// <summary>
    /// Gets the default Docker endpoint based on the operating system.
    /// </summary>
    private static Uri GetDefaultDockerEndpoint()
    {
        if (OperatingSystem.IsWindows())
        {
            return new Uri("npipe://./pipe/docker_engine");
        }
        else
        {
            return new Uri("unix:///var/run/docker.sock");
        }
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            _client.Dispose();
            _disposed = true;
        }
    }
}

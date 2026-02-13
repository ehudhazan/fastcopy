using System.Buffers;
using System.IO.Pipelines;
using k8s;
using k8s.Models;

namespace FastCopy.Transports;

/// <summary>
/// Kubernetes transport adapter that copies files into pods using tar streaming.
/// Uses NamespacedPodExecAsync with tar -xf - to receive files via stdin,
/// mimicking the kubectl cp logic for high-performance pod-to-pod or local-to-pod transfers.
/// </summary>
public sealed class K8sTransport : ITransportAdapter, IDisposable
{
    private readonly IKubernetes _client;
    private readonly long _maxBytesPerSecond;
    private bool _disposed;

    /// <summary>
    /// Creates a new K8sTransport instance.
    /// </summary>
    /// <param name="maxBytesPerSecond">Optional bandwidth limit for the transport.</param>
    /// <param name="kubeConfigPath">Optional path to kubeconfig file. Defaults to default location.</param>
    public K8sTransport(long maxBytesPerSecond = 0, string? kubeConfigPath = null)
    {
        _maxBytesPerSecond = maxBytesPerSecond;
        
        // Load kubeconfig from default location or specified path
        KubernetesClientConfiguration config;
        
        if (!string.IsNullOrEmpty(kubeConfigPath))
        {
            config = KubernetesClientConfiguration.BuildConfigFromConfigFile(kubeConfigPath);
        }
        else if (KubernetesClientConfiguration.IsInCluster())
        {
            config = KubernetesClientConfiguration.InClusterConfig();
        }
        else
        {
            config = KubernetesClientConfiguration.BuildDefaultConfig();
        }
        
        _client = new Kubernetes(config);
    }

    /// <summary>
    /// Creates a new K8sTransport instance with an explicit Kubernetes client.
    /// </summary>
    /// <param name="client">The Kubernetes client to use.</param>
    /// <param name="maxBytesPerSecond">Optional bandwidth limit for the transport.</param>
    public K8sTransport(IKubernetes client, long maxBytesPerSecond = 0)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _maxBytesPerSecond = maxBytesPerSecond;
    }

    /// <inheritdoc />
    public async ValueTask CopyStreamAsync(Stream source, string destinationPath, CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        // Parse the k8s:// URI to extract namespace, pod name, and path
        // Expected format: k8s://namespace/pod-name/path/to/file
        var (namespaceName, podName, podPath) = ParseK8sUri(destinationPath);

        // Get file info for tar header
        long fileSize = source.CanSeek ? source.Length : 0;
        
        // Create tar stream on the fly
        await using var tarStream = CreateTarStream(source, podPath, fileSize);
        
        // Extract directory and filename for tar extraction
        string? directory = Path.GetDirectoryName(podPath);
        string targetDirectory = string.IsNullOrEmpty(directory) ? "/" : directory;
        
        // Execute tar command in pod to extract the archive
        // tar -xf - extracts from stdin, -C sets the target directory
        var command = new[] { "tar", "-xf", "-", "-C", targetDirectory };
        
        // Use MuxedStreamNamespacedPodExecAsync which returns a multiplexed stream
        // for stdin, stdout, and stderr channels
        var muxedStream = await _client.MuxedStreamNamespacedPodExecAsync(
            name: podName,
            @namespace: namespaceName,
            command: command,
            container: null,
            stdin: true,
            stdout: false,
            stderr: true,
            tty: false,
            cancellationToken: ct);

        try
        {
            // Get the stdin stream from the demuxer (channel 0)
            var stdinStream = muxedStream.GetStream((byte?)0, (byte?)0);
            
            // Stream tar data to the pod's stdin  
            await StreamTarToStdinAsync(tarStream, stdinStream, ct);
            
            // Close stdin to signal completion
            stdinStream.Close();
            
            // Get stderr stream (channel 2) and read any error output
            var stderrStream = muxedStream.GetStream((byte?)2, (byte?)2);
            string? errorOutput = await ReadStreamAsync(stderrStream, ct);
            if (!string.IsNullOrEmpty(errorOutput))
            {
                throw new InvalidOperationException($"Error during tar extraction: {errorOutput}");
            }
        }
        finally
        {
            muxedStream.Dispose();
        }
    }

    /// <summary>
    /// Creates a tar stream on the fly from the source stream.
    /// </summary>
    private Stream CreateTarStream(Stream source, string fileName, long fileSize)
    {
        return new TarStream(source, fileName, fileSize, _maxBytesPerSecond);
    }

    /// <summary>
    /// Streams tar data from the source to the stdin stream.
    /// </summary>
    private async Task StreamTarToStdinAsync(
        Stream tarStream, 
        Stream stdinStream, 
        CancellationToken ct)
    {
        byte[] buffer = ArrayPool<byte>.Shared.Rent(81920); // 80KB buffer
        
        try
        {
            while (true)
            {
                int bytesRead = await tarStream.ReadAsync(buffer, 0, buffer.Length, ct);
                
                if (bytesRead == 0)
                {
                    break; // End of stream
                }
                
                await stdinStream.WriteAsync(buffer, 0, bytesRead, ct);
            }
            
            await stdinStream.FlushAsync(ct);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    /// <summary>
    /// Reads all data from a stream and returns it as a string.
    /// </summary>
    private async Task<string?> ReadStreamAsync(Stream stream, CancellationToken ct)
    {
        if (stream == null)
        {
            return null;
        }
        
        byte[] buffer = ArrayPool<byte>.Shared.Rent(4096);
        var output = new System.Text.StringBuilder();
        
        try
        {
            while (true)
            {
                int bytesRead = await stream.ReadAsync(buffer, 0, buffer.Length, ct);
                
                if (bytesRead == 0)
                {
                    break;
                }
                
                string message = System.Text.Encoding.UTF8.GetString(buffer, 0, bytesRead);
                output.Append(message);
            }
            
            return output.Length > 0 ? output.ToString() : null;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    /// <summary>
    /// Parses a k8s:// URI into namespace, pod name, and path components.
    /// </summary>
    /// <param name="destinationPath">URI in format k8s://namespace/pod-name/path/to/file</param>
    /// <returns>Tuple of (namespace, podName, podPath)</returns>
    private static (string namespaceName, string podName, string podPath) ParseK8sUri(string destinationPath)
    {
        if (!destinationPath.StartsWith("k8s://", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("Destination path must start with k8s://", nameof(destinationPath));
        }

        string pathPart = destinationPath["k8s://".Length..];
        
        // Split into components: namespace/pod-name/path/to/file
        int firstSlash = pathPart.IndexOf('/');
        if (firstSlash <= 0)
        {
            throw new ArgumentException(
                "Invalid k8s URI format. Expected: k8s://namespace/pod-name/path/to/file", 
                nameof(destinationPath));
        }

        string namespaceName = pathPart[..firstSlash];
        string remainder = pathPart[(firstSlash + 1)..];
        
        int secondSlash = remainder.IndexOf('/');
        if (secondSlash <= 0)
        {
            throw new ArgumentException(
                "Invalid k8s URI format. Expected: k8s://namespace/pod-name/path/to/file", 
                nameof(destinationPath));
        }

        string podName = remainder[..secondSlash];
        string podPath = remainder[secondSlash..];

        if (string.IsNullOrWhiteSpace(namespaceName))
        {
            throw new ArgumentException("Namespace cannot be empty", nameof(destinationPath));
        }
        
        if (string.IsNullOrWhiteSpace(podName))
        {
            throw new ArgumentException("Pod name cannot be empty", nameof(destinationPath));
        }

        return (namespaceName, podName, podPath);
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

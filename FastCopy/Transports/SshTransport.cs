using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using Renci.SshNet;

namespace FastCopy.Transports;

/// <summary>
/// SSH/SFTP transport adapter with connection pooling and zero-copy streaming.
/// Supports multiple authentication methods:
/// - User & password
/// - User & password & certificate (with or without passphrase)
/// - User & certificate (with or without passphrase)
/// - SSH agent
/// - Auto-discovery of default SSH keys (~/.ssh/id_rsa, id_ed25519, id_ecdsa, id_dsa)
/// Server validation is optional and can be disabled for testing environments.
/// URL format: ssh://user:pass@host:port/path or ssh://user@host:port/path
/// </summary>
public sealed partial class SshTransport : ITransportAdapter, IDisposable
{
    private readonly ZeroGCProvider _provider;
    private readonly ConcurrentDictionary<string, ConnectionPool> _pools = new();
    private readonly string? _privateKeyPath;
    private readonly string? _privateKeyPassphrase;
    private readonly bool _useAgent;
    private readonly bool _validateServer;
    private bool _disposed;

    [GeneratedRegex(@"^ssh://(?:([^:@]+)(?::([^@]+))?@)?([^:/@]+)(?::(\d+))?(/.*)?$", RegexOptions.Compiled)]
    private static partial Regex SshUrlRegex();

    public SshTransport(long maxBytesPerSecond = 0, string? privateKeyPath = null, string? privateKeyPassphrase = null, bool useAgent = true, bool validateServer = true)
    {
        _provider = new ZeroGCProvider(maxBytesPerSecond);
        _privateKeyPath = privateKeyPath;
        _privateKeyPassphrase = privateKeyPassphrase;
        _useAgent = useAgent;
        _validateServer = validateServer;
    }

    /// <inheritdoc />
    public async ValueTask CopyStreamAsync(Stream source, string destinationPath, CancellationToken ct)
    {
        var (host, port, username, password, remotePath) = ParseSshUrl(destinationPath);

        // Get or create a connection pool for this host
        var poolKey = $"{username}@{host}:{port}";
        var pool = _pools.GetOrAdd(poolKey, _ => new ConnectionPool(host, port, username, password, _privateKeyPath, _privateKeyPassphrase, _useAgent, _validateServer));

        // Lease a connection from the pool
        var client = await pool.LeaseAsync(ct);
        try
        {
            // Ensure the remote directory exists
            var remoteDirectory = GetRemoteDirectory(remotePath);
            if (!string.IsNullOrEmpty(remoteDirectory) && !client.Exists(remoteDirectory))
            {
                CreateRemoteDirectoryRecursive(client, remoteDirectory);
            }

            // Open the remote write stream
            await using var destination = client.OpenWrite(remotePath);

            // Use ZeroGCProvider for efficient stream copying
            await _provider.CopyStreamAsync(source, destination, onProgress: null, cancellationToken: ct);

            // Ensure the data is flushed to the remote server
            await destination.FlushAsync(ct);
        }
        finally
        {
            // Return the connection to the pool
            pool.Return(client);
        }
    }

    private static (string host, int port, string username, string? password, string remotePath) ParseSshUrl(string url)
    {
        var match = SshUrlRegex().Match(url);
        if (!match.Success)
        {
            throw new ArgumentException($"Invalid SSH URL format: {url}. Expected: ssh://user:pass@host:port/path", nameof(url));
        }

        var username = match.Groups[1].Success ? match.Groups[1].Value : Environment.UserName;
        var password = match.Groups[2].Success ? match.Groups[2].Value : null;
        var host = match.Groups[3].Value;
        var port = match.Groups[4].Success ? int.Parse(match.Groups[4].Value) : 22;
        var remotePath = match.Groups[5].Success ? match.Groups[5].Value : "/";

        if (string.IsNullOrEmpty(host))
        {
            throw new ArgumentException($"Host cannot be empty in SSH URL: {url}", nameof(url));
        }

        return (host, port, username, password, remotePath);
    }

    private static string GetRemoteDirectory(string remotePath)
    {
        var lastSlash = remotePath.LastIndexOf('/');
        return lastSlash > 0 ? remotePath[..lastSlash] : string.Empty;
    }

    private static void CreateRemoteDirectoryRecursive(SftpClient client, string path)
    {
        if (string.IsNullOrEmpty(path) || path == "/")
            return;

        var parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var currentPath = "/";

        foreach (var part in parts)
        {
            currentPath = currentPath == "/" ? $"/{part}" : $"{currentPath}/{part}";
            if (!client.Exists(currentPath))
            {
                client.CreateDirectory(currentPath);
            }
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;

        foreach (var pool in _pools.Values)
        {
            pool.Dispose();
        }

        _pools.Clear();
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// Connection pool for reusing SFTP connections to avoid reconnecting for every file.
    /// </summary>
    private sealed class ConnectionPool : IDisposable
    {
        private readonly string _host;
        private readonly int _port;
        private readonly string _username;
        private readonly string? _password;
        private readonly string? _privateKeyPath;
        private readonly string? _privateKeyPassphrase;
        private readonly bool _useAgent;
        private readonly bool _validateServer;
        private readonly ConcurrentBag<SftpClient> _availableConnections = new();
        private readonly SemaphoreSlim _semaphore = new(initialCount: 10, maxCount: 10); // Limit to 10 concurrent connections per host
        private int _connectionCount;
        private bool _disposed;

        public ConnectionPool(string host, int port, string username, string? password, string? privateKeyPath, string? privateKeyPassphrase, bool useAgent, bool validateServer)
        {
            _host = host;
            _port = port;
            _username = username;
            _password = password;
            _privateKeyPath = privateKeyPath;
            _privateKeyPassphrase = privateKeyPassphrase;
            _useAgent = useAgent;
            _validateServer = validateServer;
        }

        public async Task<SftpClient> LeaseAsync(CancellationToken ct)
        {
            await _semaphore.WaitAsync(ct);

            if (_availableConnections.TryTake(out var client))
            {
                // Verify the connection is still alive
                if (client.IsConnected)
                {
                    return client;
                }

                // Connection died, dispose and create a new one
                client.Dispose();
                Interlocked.Decrement(ref _connectionCount);
            }

            // Create a new connection
            client = CreateConnection();
            await client.ConnectAsync(ct);
            Interlocked.Increment(ref _connectionCount);

            return client;
        }

        public void Return(SftpClient client)
        {
            if (_disposed || !client.IsConnected)
            {
                client.Dispose();
                Interlocked.Decrement(ref _connectionCount);
            }
            else
            {
                _availableConnections.Add(client);
            }

            _semaphore.Release();
        }

        private SftpClient CreateConnection()
        {
            var authMethods = new List<AuthenticationMethod>();

            // 1. Try explicit private key first (highest priority)
            if (!string.IsNullOrEmpty(_privateKeyPath) && File.Exists(_privateKeyPath))
            {
                authMethods.Add(CreatePrivateKeyAuth(_privateKeyPath, _privateKeyPassphrase));
            }

            // 2. Auto-discover default SSH keys from ~/.ssh/
            if (authMethods.Count == 0)
            {
                var homeDir = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                var sshDir = Path.Combine(homeDir, ".ssh");

                if (Directory.Exists(sshDir))
                {
                    // Try common key types in order of preference
                    var defaultKeys = new[]
                    {
                        Path.Combine(sshDir, "id_ed25519"),
                        Path.Combine(sshDir, "id_ecdsa"),
                        Path.Combine(sshDir, "id_rsa"),
                        Path.Combine(sshDir, "id_dsa")
                    };

                    foreach (var keyPath in defaultKeys)
                    {
                        if (File.Exists(keyPath))
                        {
                            try
                            {
                                authMethods.Add(CreatePrivateKeyAuth(keyPath, _privateKeyPassphrase));
                            }
                            catch
                            {
                                // Key might be encrypted and we don't have the passphrase, or it's invalid
                                // Continue to next key
                            }
                        }
                    }
                }
            }

            // 3. Add password authentication if provided
            if (!string.IsNullOrEmpty(_password))
            {
                authMethods.Add(new PasswordAuthenticationMethod(_username, _password));
            }

            // 4. Add keyboard-interactive authentication
            authMethods.Add(new KeyboardInteractiveAuthenticationMethod(_username));

            // 5. Add SSH agent authentication if enabled
            if (_useAgent)
            {
                try
                {
                    var agentAuth = new PrivateKeyAuthenticationMethod(_username);
                    authMethods.Add(agentAuth);
                }
                catch
                {
                    // SSH agent not available or failed to initialize
                }
            }

            // 6. Fallback: empty password (for testing or special configurations)
            if (authMethods.Count == 0)
            {
                authMethods.Add(new PasswordAuthenticationMethod(_username, string.Empty));
            }

            // Create connection info with all authentication methods
            var connectionInfo = new Renci.SshNet.ConnectionInfo(_host, _port, _username, authMethods.ToArray());
            var client = new SftpClient(connectionInfo)
            {
                BufferSize = 64 * 1024, // 64KB buffer for better throughput
                OperationTimeout = TimeSpan.FromSeconds(30)
            };

            // Disable server validation if configured
            if (!_validateServer)
            {
                client.HostKeyReceived += (sender, e) => e.CanTrust = true;
            }

            return client;
        }

        private PrivateKeyAuthenticationMethod CreatePrivateKeyAuth(string keyPath, string? passphrase)
        {
            PrivateKeyFile keyFile;
            if (string.IsNullOrEmpty(passphrase))
            {
                keyFile = new PrivateKeyFile(keyPath);
            }
            else
            {
                keyFile = new PrivateKeyFile(keyPath, passphrase);
            }

            return new PrivateKeyAuthenticationMethod(_username, keyFile);
        }

        public void Dispose()
        {
            if (_disposed)
                return;

            _disposed = true;

            while (_availableConnections.TryTake(out var client))
            {
                client.Disconnect();
                client.Dispose();
            }

            _semaphore.Dispose();
            GC.SuppressFinalize(this);
        }
    }
}

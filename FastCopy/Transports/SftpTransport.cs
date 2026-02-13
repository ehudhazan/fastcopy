using Renci.SshNet;

namespace FastCopy.Transports;

public sealed class SftpTransport : ISshTransport
{
    private SftpClient? _client;

    public async Task ConnectAsync(string host, string username, string password, int port = 22, CancellationToken cancellationToken = default)
    {
        _client = new SftpClient(host, port, username, password);
        await _client.ConnectAsync(cancellationToken);
    }

    public Task<Stream> OpenReadAsync(string path, CancellationToken cancellationToken = default)
    {
        if (_client is null || !_client.IsConnected)
            throw new InvalidOperationException("SFTP client is not connected.");

        // OpenRead is synchronous in older versions but wrapping it in Task.FromResult is fine for the interface
        // Newer SSH.NET might have Async methods, but typically OpenRead returns a stream immediately.
        return Task.FromResult<Stream>(_client.OpenRead(path));
    }

    public Task<Stream> OpenWriteAsync(string path, CancellationToken cancellationToken = default)
    {
        if (_client is null || !_client.IsConnected)
            throw new InvalidOperationException("SFTP client is not connected.");

        return Task.FromResult<Stream>(_client.OpenWrite(path));
    }

    public bool Exists(string path)
    {
        if (_client is null || !_client.IsConnected)
            throw new InvalidOperationException("SFTP client is not connected.");
        
        return _client.Exists(path);
    }

    public void CreateDirectory(string path)
    {
        if (_client is null || !_client.IsConnected)
            throw new InvalidOperationException("SFTP client is not connected.");

        if (!_client.Exists(path))
        {
            _client.CreateDirectory(path);
        }
    }

    public void Dispose()
    {
        _client?.Disconnect();
        _client?.Dispose();
        GC.SuppressFinalize(this);
    }
}

using Renci.SshNet;

namespace FastCopy.Transports;

public interface ISshTransport : IDisposable
{
    Task ConnectAsync(string host, string username, string password, int port = 22, CancellationToken cancellationToken = default);
    Task<Stream> OpenReadAsync(string path, CancellationToken cancellationToken = default);
    Task<Stream> OpenWriteAsync(string path, CancellationToken cancellationToken = default);
    bool Exists(string path);
    void CreateDirectory(string path);
}

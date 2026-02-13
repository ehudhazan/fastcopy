using System.Threading.Tasks;

namespace FastCopy.Core;

public readonly struct PauseToken
{
    private readonly PauseTokenSource? _source;

    public PauseToken(PauseTokenSource source) => _source = source;

    public bool IsPaused => _source?.IsPaused ?? false;

    public ValueTask WaitWhilePausedAsync(CancellationToken cancellationToken = default)
    {
        return _source?.WaitWhilePausedAsync(cancellationToken) ?? ValueTask.CompletedTask;
    }
}

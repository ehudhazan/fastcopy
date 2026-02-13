using System.Threading.Tasks;

namespace FastCopy.Core;

public sealed class PauseTokenSource
{
    private volatile TaskCompletionSource? _pausedTcs;
    private readonly object _lock = new();

    public bool IsPaused => _pausedTcs != null;

    public void Pause()
    {
        lock (_lock)
        {
            if (_pausedTcs == null)
            {
                _pausedTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            }
        }
    }

    public void Resume()
    {
        lock (_lock)
        {
            _pausedTcs?.TrySetResult();
            _pausedTcs = null;
        }
    }

    public void Toggle()
    {
        lock (_lock)
        {
            if (IsPaused)
            {
                Resume();
            }
            else
            {
                Pause();
            }
        }
    }

    public ValueTask WaitWhilePausedAsync(CancellationToken cancellationToken = default)
    {
        var tcs = _pausedTcs;
        return tcs != null ? new ValueTask(tcs.Task.WaitAsync(cancellationToken)) : ValueTask.CompletedTask;
    }
}

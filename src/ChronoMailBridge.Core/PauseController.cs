namespace ChronoMailBridge.Core;

public sealed class PauseController
{
    private readonly object _gate = new();
    private TaskCompletionSource _resumeSource = CompletedSource();
    private bool _isPaused;

    public bool IsPaused
    {
        get
        {
            lock (_gate)
            {
                return _isPaused;
            }
        }
    }

    public void Pause()
    {
        lock (_gate)
        {
            if (_isPaused)
            {
                return;
            }

            _isPaused = true;
            _resumeSource = new(TaskCreationOptions.RunContinuationsAsynchronously);
        }
    }

    public void Resume()
    {
        TaskCompletionSource? source = null;
        lock (_gate)
        {
            if (!_isPaused)
            {
                return;
            }

            _isPaused = false;
            source = _resumeSource;
        }

        source.TrySetResult();
    }

    public async Task WaitIfPausedAsync(CancellationToken cancellationToken)
    {
        Task waitTask;
        lock (_gate)
        {
            waitTask = _resumeSource.Task;
        }

        await waitTask.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    private static TaskCompletionSource CompletedSource()
    {
        var source = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        source.SetResult();
        return source;
    }
}

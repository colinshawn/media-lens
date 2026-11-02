namespace MediaLens.Http;

internal sealed class ResponseHeadersDeadline : IDisposable
{
    private readonly Lock _sync = new();
    private readonly CancellationTokenSource _timeout = new();
    private readonly CancellationTokenSource _linked;
    private readonly TimeSpan _duration;
    private bool _disposed;

    internal ResponseHeadersDeadline(TimeSpan duration, CancellationToken cancellationToken)
    {
        _duration = duration;
        _linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _timeout.Token);
        Token = _linked.Token;
        Resume();
    }

    internal CancellationToken Token { get; }

    internal bool IsExpired => _timeout.IsCancellationRequested;

    internal void Pause()
    {
        lock (_sync)
        {
            if (!_disposed)
            {
                _timeout.CancelAfter(Timeout.InfiniteTimeSpan);
            }
        }
    }

    internal void Resume()
    {
        lock (_sync)
        {
            if (!_disposed)
            {
                _timeout.CancelAfter(_duration);
            }
        }
    }

    public void Dispose()
    {
        lock (_sync)
        {
            _disposed = true;
            _linked.Dispose();
            _timeout.Dispose();
        }
    }
}

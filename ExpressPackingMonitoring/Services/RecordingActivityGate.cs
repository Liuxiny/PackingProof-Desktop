namespace ExpressPackingMonitoring.Services;

internal sealed class RecordingActivityGate : IDisposable
{
    private readonly object _sync = new();
    private CancellationTokenSource _generation = new();
    private DateTimeOffset _lastActivityAt = DateTimeOffset.UtcNow;
    private int _activeOperations;
    private bool _disposed;

    public DateTimeOffset LastActivityAt
    {
        get { lock (_sync) return _lastActivityAt; }
    }

    public bool HasActiveOperation
    {
        get { lock (_sync) return _activeOperations > 0; }
    }

    public IDisposable BeginActivity()
    {
        lock (_sync)
        {
            ThrowIfDisposed();
            _activeOperations++;
            AdvanceGenerationLocked();
            return new ActivityLease(this);
        }
    }

    public void SignalActivity()
    {
        lock (_sync)
        {
            if (_disposed) return;
            AdvanceGenerationLocked();
        }
    }

    public bool IsContinuouslyIdleFor(TimeSpan duration, DateTimeOffset now)
    {
        lock (_sync)
            return !_disposed && _activeOperations == 0 && now - _lastActivityAt >= duration;
    }

    public CancellationToken GetPreemptionToken()
    {
        lock (_sync)
        {
            ThrowIfDisposed();
            return _generation.Token;
        }
    }

    private void EndActivity()
    {
        lock (_sync)
        {
            if (_disposed) return;
            _activeOperations = Math.Max(0, _activeOperations - 1);
            AdvanceGenerationLocked();
        }
    }

    private void AdvanceGenerationLocked()
    {
        _lastActivityAt = DateTimeOffset.UtcNow;
        CancellationTokenSource previous = _generation;
        _generation = new CancellationTokenSource();
        try { previous.Cancel(); } catch { }
        previous.Dispose();
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed) return;
            _disposed = true;
            try { _generation.Cancel(); } catch { }
            _generation.Dispose();
        }
    }

    private sealed class ActivityLease(RecordingActivityGate owner) : IDisposable
    {
        private RecordingActivityGate? _owner = owner;
        public void Dispose() => Interlocked.Exchange(ref _owner, null)?.EndActivity();
    }
}

using System.Collections.Concurrent;

namespace ExpressPackingMonitoring.Services;

internal static class VideoLifecycleCoordinator
{
    private static readonly ConcurrentDictionary<long, SemaphoreSlim> Locks = new();

    public static async ValueTask<IDisposable> EnterAsync(long recordId, CancellationToken cancellationToken)
    {
        SemaphoreSlim gate = Locks.GetOrAdd(recordId, static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        return new Releaser(gate);
    }

    private sealed class Releaser(SemaphoreSlim gate) : IDisposable
    {
        private SemaphoreSlim? _gate = gate;

        public void Dispose()
        {
            Interlocked.Exchange(ref _gate, null)?.Release();
        }
    }
}

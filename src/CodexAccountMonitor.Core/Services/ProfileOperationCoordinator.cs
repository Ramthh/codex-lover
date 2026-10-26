using System.Collections.Concurrent;

namespace CodexAccountMonitor.Core.Services;

/// <summary>
/// Serializes operations that may open or mutate the same isolated Codex profile.
/// Different profiles can still run concurrently.
/// </summary>
public sealed class ProfileOperationCoordinator
{
    private readonly ConcurrentDictionary<Guid, SemaphoreSlim> _gates = new();

    public async ValueTask<IDisposable> AcquireAsync(
        Guid profileId,
        CancellationToken cancellationToken = default)
    {
        var gate = _gates.GetOrAdd(profileId, static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        return new Lease(gate);
    }

    private sealed class Lease : IDisposable
    {
        private SemaphoreSlim? _gate;

        public Lease(SemaphoreSlim gate)
        {
            _gate = gate;
        }

        public void Dispose() => Interlocked.Exchange(ref _gate, null)?.Release();
    }
}

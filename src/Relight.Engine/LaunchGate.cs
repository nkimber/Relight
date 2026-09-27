namespace Relight.Engine;

public interface ILaunchGate
{
    Task<IDisposable> EnterAsync(CancellationToken cancellationToken);
}

/// <summary>
/// Bounds simultaneous launch dispatches across profiles. The permit covers
/// only dispatch, not the target's appearance or stability observation.
/// </summary>
public sealed class BoundedLaunchGate : ILaunchGate, IDisposable
{
    private readonly SemaphoreSlim _permits;

    public BoundedLaunchGate(int maximumConcurrentDispatches = 4)
    {
        if (maximumConcurrentDispatches < 2)
            throw new ArgumentOutOfRangeException(nameof(maximumConcurrentDispatches),
                "At least two slots are required so one blocked adapter cannot stall every profile.");
        _permits = new(maximumConcurrentDispatches, maximumConcurrentDispatches);
    }

    public async Task<IDisposable> EnterAsync(CancellationToken cancellationToken)
    {
        await _permits.WaitAsync(cancellationToken).ConfigureAwait(false);
        return new Lease(_permits);
    }

    public void Dispose() => _permits.Dispose();

    private sealed class Lease(SemaphoreSlim permits) : IDisposable
    {
        private int _released;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0)
                permits.Release();
        }
    }
}

internal sealed class UnboundedLaunchGate : ILaunchGate
{
    public static UnboundedLaunchGate Instance { get; } = new();
    public Task<IDisposable> EnterAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult<IDisposable>(NoopLease.Instance);
    }

    private sealed class NoopLease : IDisposable
    {
        public static NoopLease Instance { get; } = new();
        public void Dispose() { }
    }
}

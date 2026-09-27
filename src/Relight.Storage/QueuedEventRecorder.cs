using System.Threading.Channels;

namespace Relight.Storage;

public interface IEventRecorder
{
    /// <summary>Never performs disk I/O or waits for the journal.</summary>
    bool TryRecord(OperationalEvent entry);
}

public sealed record EventRecorderStatus(
    bool Degraded,
    int QueueDepth,
    long QueueDroppedCount,
    EventJournalStatus Journal,
    string? Diagnostic);

/// <summary>
/// Keeps event writes off the recovery path. The bounded queue exposes drops;
/// the underlying journal separately buffers bounded disk failures.
/// </summary>
public sealed class QueuedEventRecorder : IEventRecorder, IAsyncDisposable
{
    private readonly IOperationalEventWriter _writer;
    private readonly Channel<OperationalEvent> _queue;
    private readonly Task _worker;
    private readonly CancellationTokenSource _stop = new();
    private long _queued;
    private long _processed;
    private long _dropped;
    private string? _workerError;

    public QueuedEventRecorder(IOperationalEventWriter writer, int capacity = 1000)
    {
        ArgumentNullException.ThrowIfNull(writer);
        if (capacity < 1) throw new ArgumentOutOfRangeException(nameof(capacity));
        _writer = writer;
        _queue = Channel.CreateBounded<OperationalEvent>(new BoundedChannelOptions(capacity)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.Wait
        });
        _worker = Task.Run(DrainAsync);
    }

    public bool TryRecord(OperationalEvent entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        if (!_queue.Writer.TryWrite(entry))
        {
            Interlocked.Increment(ref _dropped);
            return false;
        }
        Interlocked.Increment(ref _queued);
        return true;
    }

    public async Task<EventRecorderStatus> GetStatusAsync(
        CancellationToken cancellationToken = default)
    {
        EventJournalStatus journal = await _writer.GetStatusAsync(cancellationToken)
            .ConfigureAwait(false);
        long waiting = Interlocked.Read(ref _queued) - Interlocked.Read(ref _processed);
        long dropped = Interlocked.Read(ref _dropped);
        string? error = Volatile.Read(ref _workerError);
        return new(journal.Degraded || dropped > 0 || error is not null,
            (int)Math.Clamp(waiting, 0, int.MaxValue), dropped, journal, error ?? journal.Diagnostic);
    }

    private async Task DrainAsync()
    {
        try
        {
            await foreach (OperationalEvent entry in _queue.Reader.ReadAllAsync(_stop.Token))
            {
                try
                {
                    await _writer.AppendAsync(entry, _stop.Token).ConfigureAwait(false);
                    Volatile.Write(ref _workerError, null);
                }
                catch (OperationCanceledException) when (_stop.IsCancellationRequested) { return; }
                catch (Exception error)
                {
                    // The writer normally reports disk failures through status.
                    // Unexpected failures are visible here; recovery still runs.
                    Volatile.Write(ref _workerError,
                        $"Event writer failed ({error.GetType().Name}).");
                    Interlocked.Increment(ref _dropped);
                }
                finally { Interlocked.Increment(ref _processed); }
            }
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
    }

    public async ValueTask DisposeAsync()
    {
        _queue.Writer.TryComplete();
        await _worker.ConfigureAwait(false);
        _stop.Dispose();
    }
}

public sealed class NullEventRecorder : IEventRecorder
{
    public static NullEventRecorder Instance { get; } = new();
    public bool TryRecord(OperationalEvent entry) => true;
}

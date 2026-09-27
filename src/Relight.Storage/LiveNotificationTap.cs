namespace Relight.Storage;

/// <summary>
/// Copies only actionable live events into a bounded in-memory buffer while
/// forwarding the complete stream to the normal non-blocking recorder.
/// Notification presentation can fail or lag without affecting recovery.
/// </summary>
public sealed class LiveNotificationTap : IEventRecorder
{
    private readonly IEventRecorder _recorder;
    private readonly int _capacity;
    private readonly Queue<OperationalEvent> _pending = new();
    private readonly object _sync = new();

    public LiveNotificationTap(IEventRecorder recorder, int capacity = 256)
    {
        ArgumentNullException.ThrowIfNull(recorder);
        if (capacity < 1) throw new ArgumentOutOfRangeException(nameof(capacity));
        _recorder = recorder;
        _capacity = capacity;
    }

    public bool TryRecord(OperationalEvent entry)
    {
        bool recorded = _recorder.TryRecord(entry);
        if (entry.Kind is not (OperationalEventKind.LockoutEntered or
            OperationalEventKind.ObservationCompleted)) return recorded;
        lock (_sync)
        {
            if (_pending.Count >= _capacity) _pending.Dequeue();
            _pending.Enqueue(entry);
        }
        return recorded;
    }

    public IReadOnlyList<OperationalEvent> Drain()
    {
        lock (_sync)
        {
            OperationalEvent[] result = _pending.ToArray();
            _pending.Clear();
            return result;
        }
    }
}

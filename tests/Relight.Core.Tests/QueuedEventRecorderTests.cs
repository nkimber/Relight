using Relight.Storage;

namespace Relight.Core.Tests;

public sealed class QueuedEventRecorderTests
{
    [Fact]
    public async Task Full_queue_reports_a_drop_without_blocking_the_caller()
    {
        var writer = new BlockingWriter();
        await using var recorder = new QueuedEventRecorder(writer, capacity: 2);
        Assert.True(recorder.TryRecord(NewEvent()));
        await writer.Entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.True(recorder.TryRecord(NewEvent()));
        Assert.True(recorder.TryRecord(NewEvent()));
        Assert.False(recorder.TryRecord(NewEvent()));
        EventRecorderStatus status = await recorder.GetStatusAsync();
        Assert.True(status.Degraded);
        Assert.Equal(1, status.QueueDroppedCount);
        writer.Release.TrySetResult();
    }

    private static OperationalEvent NewEvent() =>
        new(DateTimeOffset.UtcNow, Guid.NewGuid(), EventSeverity.Information,
            OperationalEventKind.Startup);

    private sealed class BlockingWriter : IOperationalEventWriter
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<EventJournalStatus> AppendAsync(OperationalEvent entry,
            CancellationToken cancellationToken = default)
        {
            Entered.TrySetResult();
            await Release.Task.WaitAsync(cancellationToken);
            return new(false, 0, 0, null);
        }
        public Task<EventJournalStatus> GetStatusAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new EventJournalStatus(false, 0, 0, null));
    }
}

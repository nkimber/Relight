using Relight.Core;
using Relight.Storage;

namespace Relight.Core.Tests;

public sealed class RecoveryStateStoreTests
{
    [Fact]
    public void Reserved_third_attempt_survives_restart_without_refund_or_immediate_launch()
    {
        using var directory = new TemporaryDirectory();
        var store = new RecoveryStateStore(directory.Path);
        Guid profile = Guid.NewGuid();
        var machine = new RecoveryMachine(RecoveryPolicy.Default);
        var initial = store.Create(profile, machine.ExportCheckpoint());
        Assert.Equal(1, initial.Revision);

        // Represents the write that must happen before dispatch. A process exit
        // immediately afterward cannot grant a fourth attempt on restart.
        Guid operation = Guid.NewGuid();
        var reserved = new RecoveryCheckpoint(true, false, true, false, 3,
            Guid.NewGuid(), RecoveryState.Starting, operation);
        var committed = store.Save(profile, initial.Revision, reserved);
        Assert.Equal(2, committed.Revision);
        var restored = RecoveryMachine.Restore(RecoveryPolicy.Default,
            new RecoveryStateStore(directory.Path).Load(profile).Checkpoint);
        Assert.Equal(3, restored.Snapshot.ReservedAutomaticAttempts);
        Assert.True(restored.Snapshot.LockedOut);
        Assert.Equal(RecoveryState.AwaitingIntervention, restored.Snapshot.State);
        Assert.Equal(RecoverySignal.None,
            restored.Advance(Detection.Absent(), TimeSpan.Zero).Signal);
        Assert.Throws<InvalidOperationException>(() =>
            restored.ReserveAutomaticAttempt(TimeSpan.FromSeconds(30), Guid.NewGuid()));
    }

    [Fact]
    public void Earlier_reservation_restarts_with_fresh_discovery_and_delay()
    {
        using var directory = new TemporaryDirectory();
        var store = new RecoveryStateStore(directory.Path);
        Guid profile = Guid.NewGuid();
        var state = store.Create(profile,
            new RecoveryCheckpoint(true, false, true, false, 1,
                Guid.NewGuid(), RecoveryState.Starting, Guid.NewGuid()));
        var restored = RecoveryMachine.Restore(RecoveryPolicy.Default, store.Load(profile).Checkpoint);
        Assert.Equal(RecoveryState.RetryWaiting, restored.Snapshot.State);
        Assert.Null(restored.Snapshot.RetryDeadline);
        restored.Advance(Detection.Absent(), TimeSpan.Zero);
        restored.Advance(Detection.Absent(), TimeSpan.FromSeconds(2));
        Assert.Equal(RecoverySignal.None,
            restored.Advance(Detection.Absent(), TimeSpan.FromSeconds(31)).Signal);
        Assert.Equal(RecoverySignal.LaunchDue,
            restored.Advance(Detection.Absent(), TimeSpan.FromSeconds(32)).Signal);
        Assert.Equal(1, restored.Snapshot.ReservedAutomaticAttempts);
        Assert.Equal(1, state.Revision);
    }

    [Fact]
    public void Missing_or_corrupt_state_preserves_evidence_and_fails_closed()
    {
        using var directory = new TemporaryDirectory();
        var store = new RecoveryStateStore(directory.Path);
        Guid profile = Guid.NewGuid();
        Assert.Throws<RecoveryStateUnavailableException>(() => store.Load(profile));
        var initial = store.Create(profile, new RecoveryMachine(RecoveryPolicy.Default).ExportCheckpoint());
        store.Save(profile, initial.Revision, initial.Checkpoint with { ReservedAutomaticAttempts = 1 });
        string file = Path.Combine(directory.Path, "State", profile.ToString("N") + ".json");
        string corrupted = File.ReadAllText(file).Replace("\"reservedAutomaticAttempts\": 1",
            "\"reservedAutomaticAttempts\": 0", StringComparison.Ordinal);
        Assert.NotEqual(File.ReadAllText(file), corrupted);
        File.WriteAllText(file, corrupted);
        Assert.Throws<RecoveryStateUnavailableException>(() => store.Load(profile));
        Assert.Equal(corrupted, File.ReadAllText(file));
        Assert.True(File.Exists(file + ".bak"));
        Assert.Throws<RecoveryStateUnavailableException>(() =>
            store.Save(profile, 2, initial.Checkpoint));
    }

    [Fact]
    public void Stale_revision_cannot_replace_newer_attempt_state()
    {
        using var directory = new TemporaryDirectory();
        var store = new RecoveryStateStore(directory.Path);
        Guid profile = Guid.NewGuid();
        var initial = store.Create(profile, new RecoveryMachine(RecoveryPolicy.Default).ExportCheckpoint());
        store.Save(profile, initial.Revision, initial.Checkpoint with { ReservedAutomaticAttempts = 1 });
        Assert.Throws<StaleRecoveryRevisionException>(() =>
            store.Save(profile, initial.Revision, initial.Checkpoint));
        Assert.Equal(1, store.Load(profile).Checkpoint.ReservedAutomaticAttempts);
        Assert.Throws<InvalidOperationException>(() => store.Create(profile, initial.Checkpoint));
    }

    [Fact]
    public void Unknown_schema_is_preserved_and_cannot_migrate_by_guessing()
    {
        using var directory = new TemporaryDirectory();
        var store = new RecoveryStateStore(directory.Path);
        Guid profile = Guid.NewGuid();
        store.Create(profile, new RecoveryMachine(RecoveryPolicy.Default).ExportCheckpoint());
        string file = Path.Combine(directory.Path, "State", profile.ToString("N") + ".json");
        string unsupported = File.ReadAllText(file).Replace("\"schemaVersion\": 1",
            "\"schemaVersion\": 999", StringComparison.Ordinal);
        File.WriteAllText(file, unsupported);
        Assert.Throws<RecoveryStateUnavailableException>(() => store.Load(profile));
        Assert.Equal(unsupported, File.ReadAllText(file));
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), $"relight-store-{Guid.NewGuid():N}");

        public TemporaryDirectory() => Directory.CreateDirectory(Path);

        public void Dispose()
        {
            // The path is generated under the task's dedicated temp directory.
            if (Directory.Exists(Path)) Directory.Delete(Path, recursive: true);
        }
    }
}

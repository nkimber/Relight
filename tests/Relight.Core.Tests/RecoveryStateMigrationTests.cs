using Relight.Core;
using Relight.Storage;

namespace Relight.Core.Tests;

public sealed class RecoveryStateMigrationTests
{
    private const string SessionKey = "CCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCC";

    [Fact]
    public void Migration_preserves_legacy_evidence_and_blocks_upgraded_legacy_writers()
    {
        using var directory = new TestDirectory();
        Guid profile = Guid.NewGuid();
        Guid episode = Guid.NewGuid();
        var legacy = new RecoveryStateStore(directory.Path);
        StoredRecoveryState old = legacy.Create(profile,
            new RecoveryCheckpoint(true, true, true, true, 2, episode,
                RecoveryState.AwaitingIntervention, null));
        string legacyPath = Path.Combine(directory.Path, "State", $"{profile:N}.json");
        byte[] original = File.ReadAllBytes(legacyPath);
        var budgets = new SharedRecoveryBudgetStore(directory.Path);
        var session = new RecoverySessionStateStore(directory.Path, SessionKey, budgets);

        legacy.MigrateToSession(profile, budgets, session);

        SharedRecoveryBudget shared = budgets.Load(profile);
        RecoveryCheckpoint live = session.Load(profile).Checkpoint;
        Assert.Equal(2, shared.ReservedAutomaticAttempts);
        Assert.Equal(episode, shared.EpisodeId);
        Assert.True(shared.LockedOut);
        Assert.True(live.Paused);
        Assert.False(live.Armed);
        Assert.Equal(RecoveryState.AwaitingIntervention, live.LastState);
        Assert.Null(live.PendingOperationId);
        Assert.Equal(shared.Revision, live.SharedBudgetRevision);
        Assert.Equal(original, File.ReadAllBytes(legacyPath));
        Assert.Contains("session-owner", File.ReadAllText(Path.Combine(
            directory.Path, "State", $"{profile:N}.owner")));
        Assert.Throws<RecoveryStateUnavailableException>(() => legacy.Load(profile));
        Assert.Throws<RecoveryStateUnavailableException>(() =>
            legacy.Save(profile, old.Revision, old.Checkpoint));
        Assert.Throws<RecoveryStateUnavailableException>(() =>
            legacy.Create(profile, old.Checkpoint));
        Assert.Equal(original, File.ReadAllBytes(legacyPath));
    }

    [Fact]
    public void Interrupted_migration_leaves_legacy_writer_suspended_and_original_untouched()
    {
        using var directory = new TestDirectory();
        Guid profile = Guid.NewGuid();
        var legacy = new RecoveryStateStore(directory.Path);
        StoredRecoveryState old = legacy.Create(profile,
            new RecoveryCheckpoint(true, false, true, false, 0, null,
                RecoveryState.Healthy, null));
        string legacyPath = Path.Combine(directory.Path, "State", $"{profile:N}.json");
        byte[] original = File.ReadAllBytes(legacyPath);
        var budgets = new SharedRecoveryBudgetStore(directory.Path);
        budgets.Create(profile);
        var session = new RecoverySessionStateStore(directory.Path, SessionKey, budgets);

        Assert.Throws<InvalidOperationException>(() =>
            legacy.MigrateToSession(profile, budgets, session));

        Assert.Contains("migration-pending", File.ReadAllText(Path.Combine(
            directory.Path, "State", $"{profile:N}.owner")));
        Assert.Throws<RecoveryStateUnavailableException>(() => legacy.Load(profile));
        Assert.Throws<RecoveryStateUnavailableException>(() =>
            legacy.Save(profile, old.Revision, old.Checkpoint));
        Assert.Throws<RecoveryStateUnavailableException>(() =>
            session.Load(profile));
        Assert.Equal(original, File.ReadAllBytes(legacyPath));
    }

    [Fact]
    public void Damaged_legacy_state_does_not_start_a_migration()
    {
        using var directory = new TestDirectory();
        Guid profile = Guid.NewGuid();
        var legacy = new RecoveryStateStore(directory.Path);
        legacy.Create(profile, new RecoveryCheckpoint(true, false, false, false,
            0, null, RecoveryState.WaitingForFirstStart, null));
        string legacyPath = Path.Combine(directory.Path, "State", $"{profile:N}.json");
        File.WriteAllText(legacyPath, "damaged original");
        var budgets = new SharedRecoveryBudgetStore(directory.Path);
        var session = new RecoverySessionStateStore(directory.Path, SessionKey, budgets);

        Assert.Throws<RecoveryStateUnavailableException>(() =>
            legacy.MigrateToSession(profile, budgets, session));
        Assert.Equal("damaged original", File.ReadAllText(legacyPath));
        Assert.False(File.Exists(Path.Combine(directory.Path, "State",
            $"{profile:N}.owner")));
        Assert.Throws<RecoveryStateUnavailableException>(() => budgets.Load(profile));
    }

    private sealed class TestDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), $"relight-state-migration-{Guid.NewGuid():N}");
        public TestDirectory() => Directory.CreateDirectory(Path);
        public void Dispose()
        {
            if (Directory.Exists(Path)) Directory.Delete(Path, recursive: true);
        }
    }
}

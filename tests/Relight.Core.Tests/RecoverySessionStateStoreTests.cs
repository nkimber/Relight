using Relight.Core;
using Relight.Storage;

namespace Relight.Core.Tests;

public sealed class RecoverySessionStateStoreTests
{
    private const string FirstKey = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";
    private const string SecondKey = "BBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBB";

    [Fact]
    public void Two_sign_ins_keep_live_pause_state_separate_but_share_budget()
    {
        using var directory = new TestDirectory();
        Guid profile = Guid.NewGuid();
        var budgets = new SharedRecoveryBudgetStore(directory.Path);
        budgets.Create(profile);
        var first = new RecoverySessionStateStore(directory.Path, FirstKey, budgets);
        var second = new RecoverySessionStateStore(directory.Path, SecondKey, budgets);
        StoredRecoveryState firstState = first.InitializeForNewSignIn(profile, enabled: true);
        StoredRecoveryState secondState = second.InitializeForNewSignIn(profile, enabled: true);

        first.Save(profile, firstState.Revision,
            firstState.Checkpoint with { Paused = true });
        Assert.True(first.Load(profile).Checkpoint.Paused);
        Assert.False(second.Load(profile).Checkpoint.Paused);
        Assert.Equal(1, first.Load(profile).Checkpoint.SharedBudgetRevision);
        Assert.Equal(0, second.Load(profile).Checkpoint.ReservedAutomaticAttempts);
        Assert.True(File.Exists(Path.Combine(directory.Path, "Sessions", FirstKey,
            "State", $"{profile:N}.json")));
        Assert.True(File.Exists(Path.Combine(directory.Path, "Sessions", SecondKey,
            "State", $"{profile:N}.json")));
    }

    [Fact]
    public void Shared_budget_change_rejects_stale_session_saves_until_commit_is_adopted()
    {
        using var directory = new TestDirectory();
        Guid profile = Guid.NewGuid();
        Guid episode = Guid.NewGuid();
        Guid operation = Guid.NewGuid();
        var budgets = new SharedRecoveryBudgetStore(directory.Path);
        SharedRecoveryBudget created = budgets.Create(profile);
        var first = new RecoverySessionStateStore(directory.Path, FirstKey, budgets);
        var second = new RecoverySessionStateStore(directory.Path, SecondKey, budgets);
        StoredRecoveryState firstState = first.InitializeForNewSignIn(profile, true);
        StoredRecoveryState secondState = second.InitializeForNewSignIn(profile, true);

        SharedRecoveryBudget reserved = budgets.ReserveAutomatic(profile,
            created.Revision, 3, episode, operation);
        Assert.Throws<StaleRecoveryRevisionException>(() =>
            second.Save(profile, secondState.Revision,
                secondState.Checkpoint with { Paused = true }));
        Assert.Throws<StaleRecoveryRevisionException>(() => second.Load(profile));
        Assert.Throws<StaleRecoveryRevisionException>(() =>
            first.Save(profile, firstState.Revision,
                firstState.Checkpoint with
                {
                    ReservedAutomaticAttempts = 1,
                    EpisodeId = episode,
                    LastState = RecoveryState.Starting,
                    PendingOperationId = operation
                }));

        first.AdoptCommittedBudget(reserved);
        StoredRecoveryState saved = first.Save(profile, firstState.Revision,
            firstState.Checkpoint with
            {
                ReservedAutomaticAttempts = 1,
                EpisodeId = episode,
                LastState = RecoveryState.Starting,
                PendingOperationId = operation
            });
        Assert.Equal(1, saved.Checkpoint.ReservedAutomaticAttempts);
        Assert.Equal(reserved.Revision, saved.Checkpoint.SharedBudgetRevision);
        Assert.Equal(operation, first.Load(profile).Checkpoint.PendingOperationId);
    }

    [Fact]
    public void Missing_budget_or_invalid_session_key_cannot_create_live_state()
    {
        using var directory = new TestDirectory();
        var budgets = new SharedRecoveryBudgetStore(directory.Path);
        Assert.Throws<ArgumentException>(() =>
            new RecoverySessionStateStore(directory.Path, "../outside", budgets));
        var session = new RecoverySessionStateStore(directory.Path, FirstKey, budgets);
        Guid profile = Guid.NewGuid();
        Assert.Throws<RecoveryStateUnavailableException>(() =>
            session.InitializeForNewSignIn(profile, true));
        Assert.False(File.Exists(Path.Combine(directory.Path, "Sessions", FirstKey,
            "State", $"{profile:N}.json")));
    }

    [Fact]
    public void New_sign_in_does_not_claim_another_sessions_pending_operation()
    {
        using var directory = new TestDirectory();
        Guid profile = Guid.NewGuid();
        Guid episode = Guid.NewGuid();
        Guid operation = Guid.NewGuid();
        var budgets = new SharedRecoveryBudgetStore(directory.Path);
        SharedRecoveryBudget created = budgets.Create(profile);
        SharedRecoveryBudget reserved = budgets.ReserveAutomatic(profile,
            created.Revision, 3, episode, operation);
        var second = new RecoverySessionStateStore(directory.Path, SecondKey, budgets);

        RecoveryCheckpoint checkpoint = second.InitializeForNewSignIn(profile, true).Checkpoint;

        Assert.False(checkpoint.Armed);
        Assert.Equal(RecoveryState.WaitingForFirstStart, checkpoint.LastState);
        Assert.Null(checkpoint.PendingOperationId);
        Assert.Null(checkpoint.PendingExplicitStart);
        Assert.Equal(reserved.ReservedAutomaticAttempts, checkpoint.ReservedAutomaticAttempts);
        Assert.Equal(reserved.EpisodeId, checkpoint.EpisodeId);
        Assert.Equal(reserved.Revision, checkpoint.SharedBudgetRevision);
        Assert.Equal(operation, budgets.Load(profile).PendingAutomaticOperationId);
    }

    [Fact]
    public void Episode_start_is_committed_shared_before_live_retry_state()
    {
        using var directory = new TestDirectory();
        Guid profile = Guid.NewGuid();
        Guid episode = Guid.NewGuid();
        var budgets = new SharedRecoveryBudgetStore(directory.Path);
        SharedRecoveryBudget initialBudget = budgets.Create(profile);
        var session = new RecoverySessionStateStore(directory.Path, FirstKey, budgets);
        StoredRecoveryState initial = session.InitializeForNewSignIn(profile, true);

        Assert.Throws<StaleRecoveryRevisionException>(() => session.Save(profile,
            initial.Revision, initial.Checkpoint with
            {
                Armed = true,
                EpisodeId = episode,
                LastState = RecoveryState.RetryWaiting
            }));

        SharedRecoveryBudget begun = budgets.BeginEpisode(profile,
            initialBudget.Revision, episode);
        session.AdoptCommittedBudget(begun);
        StoredRecoveryState saved = session.Save(profile, initial.Revision,
            initial.Checkpoint with
            {
                Armed = true,
                EpisodeId = episode,
                LastState = RecoveryState.RetryWaiting
            });
        Assert.Equal(episode, saved.Checkpoint.EpisodeId);
        Assert.Equal(begun.Revision, saved.Checkpoint.SharedBudgetRevision);
    }

    private sealed class TestDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), $"relight-session-state-{Guid.NewGuid():N}");
        public TestDirectory() => Directory.CreateDirectory(Path);
        public void Dispose()
        {
            if (Directory.Exists(Path)) Directory.Delete(Path, recursive: true);
        }
    }
}

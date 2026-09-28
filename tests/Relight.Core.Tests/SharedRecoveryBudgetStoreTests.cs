using Relight.Core;
using Relight.Storage;

namespace Relight.Core.Tests;

public sealed class SharedRecoveryBudgetStoreTests
{
    [Fact]
    public async Task Competing_final_reservations_commit_only_one_attempt()
    {
        using var directory = new TestDirectory();
        Guid profile = Guid.NewGuid();
        Guid episode = Guid.NewGuid();
        var firstSession = new SharedRecoveryBudgetStore(directory.Path);
        var secondSession = new SharedRecoveryBudgetStore(directory.Path);
        SharedRecoveryBudget budget = firstSession.Create(profile);
        for (int attempt = 0; attempt < 2; attempt++)
        {
            Guid operation = Guid.NewGuid();
            budget = firstSession.ReserveAutomatic(profile, budget.Revision, 3,
                episode, operation);
            budget = firstSession.ResolveAutomatic(profile, budget.Revision, operation);
        }

        long competingRevision = budget.Revision;
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<(SharedRecoveryBudget? Budget, Exception? Error)> Compete(
            SharedRecoveryBudgetStore contender) => Task.Run(async () =>
        {
            await release.Task;
            try
            {
                return ((SharedRecoveryBudget?)contender.ReserveAutomatic(profile,
                    competingRevision, 3, episode, Guid.NewGuid()), (Exception?)null);
            }
            catch (Exception error)
            {
                return ((SharedRecoveryBudget?)null, (Exception?)error);
            }
        });
        Task<(SharedRecoveryBudget? Budget, Exception? Error)> first = Compete(firstSession);
        Task<(SharedRecoveryBudget? Budget, Exception? Error)> second = Compete(secondSession);
        release.SetResult();
        var results = await Task.WhenAll(first, second);
        Assert.Single(results, result => result.Budget is not null);
        Assert.Single(results, result => result.Error is
            StaleRecoveryRevisionException or RecoveryStateUnavailableException);
        budget = firstSession.Load(profile);
        Assert.Equal(3, budget.ReservedAutomaticAttempts);
        Assert.NotNull(budget.PendingAutomaticOperationId);
        budget = firstSession.ResolveAutomatic(profile, budget.Revision,
            budget.PendingAutomaticOperationId.Value);
        Assert.Throws<InvalidOperationException>(() =>
            secondSession.ReserveAutomatic(profile, budget.Revision, 3,
                episode, Guid.NewGuid()));
        Assert.Equal(3, secondSession.Load(profile).ReservedAutomaticAttempts);
    }

    [Fact]
    public void Concurrent_episode_starts_cannot_create_two_authoritative_episodes()
    {
        using var directory = new TestDirectory();
        Guid profile = Guid.NewGuid();
        var firstSession = new SharedRecoveryBudgetStore(directory.Path);
        var secondSession = new SharedRecoveryBudgetStore(directory.Path);
        SharedRecoveryBudget initial = firstSession.Create(profile);
        Guid firstEpisode = Guid.NewGuid();
        Guid secondEpisode = Guid.NewGuid();

        SharedRecoveryBudget begun = firstSession.BeginEpisode(profile,
            initial.Revision, firstEpisode);
        Assert.Throws<StaleRecoveryRevisionException>(() =>
            secondSession.BeginEpisode(profile, initial.Revision, secondEpisode));
        Assert.Equal(firstEpisode, secondSession.Load(profile).EpisodeId);
        Assert.Throws<InvalidOperationException>(() =>
            secondSession.BeginEpisode(profile, begun.Revision, secondEpisode));
        SharedRecoveryBudget reserved = secondSession.ReserveAutomatic(profile,
            begun.Revision, 3, firstEpisode, Guid.NewGuid());
        Assert.Equal(firstEpisode, reserved.EpisodeId);
        Assert.Equal(1, reserved.ReservedAutomaticAttempts);
    }

    [Fact]
    public void Interrupted_reservation_is_not_refunded_and_blocks_another_dispatch()
    {
        using var directory = new TestDirectory();
        Guid profile = Guid.NewGuid();
        Guid episode = Guid.NewGuid();
        Guid operation = Guid.NewGuid();
        var firstSession = new SharedRecoveryBudgetStore(directory.Path);
        SharedRecoveryBudget created = firstSession.Create(profile);
        firstSession.ReserveAutomatic(profile, created.Revision, 3, episode, operation);

        var restarted = new SharedRecoveryBudgetStore(directory.Path);
        SharedRecoveryBudget pending = restarted.Load(profile);
        Assert.Equal(1, pending.ReservedAutomaticAttempts);
        Assert.Equal(operation, pending.PendingAutomaticOperationId);
        Assert.Throws<InvalidOperationException>(() =>
            restarted.ReserveAutomatic(profile, pending.Revision, 3,
                episode, Guid.NewGuid()));
        Assert.Throws<StaleRecoveryRevisionException>(() =>
            restarted.ResolveAutomatic(profile, pending.Revision, Guid.NewGuid()));
        SharedRecoveryBudget resolved = restarted.ResolveAutomatic(profile,
            pending.Revision, operation);
        Assert.Equal(1, resolved.ReservedAutomaticAttempts);
    }

    [Fact]
    public void Lockout_survives_limit_change_until_matching_stable_episode_or_explicit_reset()
    {
        using var directory = new TestDirectory();
        Guid profile = Guid.NewGuid();
        Guid episode = Guid.NewGuid();
        var store = new SharedRecoveryBudgetStore(directory.Path);
        SharedRecoveryBudget created = store.Create(profile);
        Guid operation = Guid.NewGuid();
        SharedRecoveryBudget reserved = store.ReserveAutomatic(profile, created.Revision,
            1, episode, operation);
        SharedRecoveryBudget resolved = store.ResolveAutomatic(profile,
            reserved.Revision, operation);
        SharedRecoveryBudget locked = store.EnterLockout(profile, resolved.Revision,
            episode);
        Assert.Throws<InvalidOperationException>(() =>
            store.ReserveAutomatic(profile, locked.Revision, 2,
                episode, Guid.NewGuid()));
        Assert.Throws<StaleRecoveryRevisionException>(() =>
            store.CompleteStableObservation(profile, locked.Revision, Guid.NewGuid()));
        SharedRecoveryBudget rearmed = store.CompleteStableObservation(profile,
            locked.Revision, episode);
        Assert.False(rearmed.LockedOut);
        Assert.Equal(0, rearmed.ReservedAutomaticAttempts);
        Assert.Null(rearmed.EpisodeId);

        Guid secondOperation = Guid.NewGuid();
        SharedRecoveryBudget second = store.ReserveAutomatic(profile,
            rearmed.Revision, 1, Guid.NewGuid(), secondOperation);
        Assert.Throws<InvalidOperationException>(() =>
            store.ResetExplicitly(profile, second.Revision));
        SharedRecoveryBudget secondResolved = store.ResolveAutomatic(profile,
            second.Revision, secondOperation);
        SharedRecoveryBudget reset = store.ResetExplicitly(profile, secondResolved.Revision);
        Assert.Equal(0, reset.ReservedAutomaticAttempts);
        Assert.Null(reset.PendingAutomaticOperationId);
    }

    [Fact]
    public void Missing_or_corrupt_shared_budget_fails_closed_without_overwriting_evidence()
    {
        using var directory = new TestDirectory();
        Guid profile = Guid.NewGuid();
        var store = new SharedRecoveryBudgetStore(directory.Path);
        Assert.Throws<RecoveryStateUnavailableException>(() => store.Load(profile));
        SharedRecoveryBudget created = store.Create(profile);
        store.ReserveAutomatic(profile, created.Revision, 3,
            Guid.NewGuid(), Guid.NewGuid());
        string path = Path.Combine(directory.Path, "Budgets", $"{profile:N}.json");
        string corrupted = File.ReadAllText(path).Replace(
            "\"reservedAutomaticAttempts\": 1",
            "\"reservedAutomaticAttempts\": 0", StringComparison.Ordinal);
        Assert.NotEqual(File.ReadAllText(path), corrupted);
        File.WriteAllText(path, corrupted);

        Assert.Throws<RecoveryStateUnavailableException>(() => store.Load(profile));
        Assert.Throws<RecoveryStateUnavailableException>(() =>
            store.ResetExplicitly(profile, 2));
        Assert.Equal(corrupted, File.ReadAllText(path));
        Assert.True(File.Exists(path + ".bak"));
        File.Delete(path);
        Assert.Throws<InvalidOperationException>(() => store.Create(profile));
        Assert.False(File.Exists(path));
    }

    [Fact]
    public void Legacy_import_preserves_attempts_lockout_and_interrupted_operation()
    {
        using var directory = new TestDirectory();
        Guid profile = Guid.NewGuid();
        Guid episode = Guid.NewGuid();
        Guid operation = Guid.NewGuid();
        var legacyStore = new RecoveryStateStore(directory.Path);
        StoredRecoveryState legacy = legacyStore.Create(profile,
            new RecoveryCheckpoint(true, false, true, true, 3, episode,
                RecoveryState.Starting, operation));
        string legacyPath = Path.Combine(directory.Path, "State", $"{profile:N}.json");
        byte[] original = File.ReadAllBytes(legacyPath);

        var budgets = new SharedRecoveryBudgetStore(directory.Path);
        SharedRecoveryBudget imported = budgets.ImportLegacy(legacy);
        Assert.Equal(3, imported.ReservedAutomaticAttempts);
        Assert.True(imported.LockedOut);
        Assert.Equal(episode, imported.EpisodeId);
        Assert.Equal(operation, imported.PendingAutomaticOperationId);
        Assert.Equal(original, File.ReadAllBytes(legacyPath));
        Assert.Throws<InvalidOperationException>(() => budgets.ImportLegacy(legacy));
        Assert.Equal(original, File.ReadAllBytes(legacyPath));
    }

    [Fact]
    public void Explicit_launch_marker_never_spends_or_releases_automatic_budget()
    {
        using var directory = new TestDirectory();
        Guid profile = Guid.NewGuid();
        Guid episode = Guid.NewGuid();
        var store = new SharedRecoveryBudgetStore(directory.Path);
        SharedRecoveryBudget created = store.Create(profile);
        Guid automatic = Guid.NewGuid();
        SharedRecoveryBudget reserved = store.ReserveAutomatic(profile,
            created.Revision, 1, episode, automatic);
        SharedRecoveryBudget resolved = store.ResolveAutomatic(profile,
            reserved.Revision, automatic);
        SharedRecoveryBudget locked = store.EnterLockout(profile,
            resolved.Revision, episode);

        Guid explicitOperation = Guid.NewGuid();
        SharedRecoveryBudget pending = store.MarkExplicitStart(profile,
            locked.Revision, explicitOperation);
        Assert.True(pending.LockedOut);
        Assert.Equal(1, pending.ReservedAutomaticAttempts);
        Assert.Throws<InvalidOperationException>(() =>
            store.CompleteStableObservation(profile, pending.Revision, episode));
        Assert.Throws<InvalidOperationException>(() =>
            store.ResetExplicitly(profile, pending.Revision));
        SharedRecoveryBudget finished = store.ResolveExplicitStart(profile,
            pending.Revision, explicitOperation);
        Assert.True(finished.LockedOut);
        Assert.Equal(1, finished.ReservedAutomaticAttempts);
    }

    [Fact]
    public void Stable_observation_cannot_refund_an_unresolved_automatic_reservation()
    {
        using var directory = new TestDirectory();
        Guid profile = Guid.NewGuid();
        Guid episode = Guid.NewGuid();
        Guid operation = Guid.NewGuid();
        var store = new SharedRecoveryBudgetStore(directory.Path);
        SharedRecoveryBudget created = store.Create(profile);
        SharedRecoveryBudget reserved = store.ReserveAutomatic(profile,
            created.Revision, 3, episode, operation);

        Assert.Throws<InvalidOperationException>(() =>
            store.CompleteStableObservation(profile, reserved.Revision, episode));
        SharedRecoveryBudget unchanged = store.Load(profile);
        Assert.Equal(reserved, unchanged);

        SharedRecoveryBudget resolved = store.ResolveAutomatic(profile,
            unchanged.Revision, operation);
        SharedRecoveryBudget stable = store.CompleteStableObservation(profile,
            resolved.Revision, episode);
        Assert.Equal(0, stable.ReservedAutomaticAttempts);
        Assert.Null(stable.EpisodeId);
    }

    private sealed class TestDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), $"relight-shared-budget-{Guid.NewGuid():N}");
        public TestDirectory() => Directory.CreateDirectory(Path);
        public void Dispose()
        {
            if (Directory.Exists(Path)) Directory.Delete(Path, recursive: true);
        }
    }
}

using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using Relight.Core;

namespace Relight.Storage;

/// <summary>
/// Stores live recovery state under an opaque logon-session key. It refuses to
/// read or write a session checkpoint if the shared budget is missing or has
/// moved ahead. The coordinator must commit shared budget changes first.
/// </summary>
public sealed class RecoverySessionStateStore : IRecoveryStateStore
{
    private static readonly Regex StorageKeyPattern = new("^[0-9A-F]{32}$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private readonly RecoveryStateStore _session;
    private readonly SharedRecoveryBudgetStore _budgets;
    private readonly ConcurrentDictionary<Guid, long> _knownBudgetRevisions = new();

    public RecoverySessionStateStore(string dataDirectory, string storageKey,
        SharedRecoveryBudgetStore budgets)
    {
        if (string.IsNullOrWhiteSpace(dataDirectory))
            throw new ArgumentException("Data directory is required.", nameof(dataDirectory));
        if (storageKey is null || !StorageKeyPattern.IsMatch(storageKey))
            throw new ArgumentException("An opaque logon-session key is required.",
                nameof(storageKey));
        _budgets = budgets ?? throw new ArgumentNullException(nameof(budgets));
        _session = new RecoveryStateStore(Path.Combine(Path.GetFullPath(dataDirectory),
            "Sessions", storageKey));
    }

    public StoredRecoveryState InitializeForNewSignIn(Guid profileId, bool enabled)
    {
        SharedRecoveryBudget budget = _budgets.Load(profileId);
        RecoveryState state = !enabled ? RecoveryState.Disabled :
            budget.LockedOut ? RecoveryState.AwaitingIntervention :
            RecoveryState.WaitingForFirstStart;
        var initial = new RecoveryCheckpoint(enabled, false, false,
            budget.LockedOut, budget.ReservedAutomaticAttempts, budget.EpisodeId,
            state, null,
            SharedBudgetRevision: budget.Revision);
        StoredRecoveryState created = _session.Create(profileId, initial);
        _knownBudgetRevisions[profileId] = budget.Revision;
        return created;
    }

    public StoredRecoveryState Create(Guid profileId, RecoveryCheckpoint initial)
    {
        SharedRecoveryBudget budget = _budgets.Load(profileId);
        EnsureSharedBudgetFieldsMatch(initial, budget);
        StoredRecoveryState created = _session.Create(profileId,
            initial with { SharedBudgetRevision = budget.Revision });
        _knownBudgetRevisions[profileId] = budget.Revision;
        return created;
    }

    public StoredRecoveryState Load(Guid profileId)
    {
        SharedRecoveryBudget budget = _budgets.Load(profileId);
        StoredRecoveryState local = _session.Load(profileId);
        EnsureSharedBudgetMatches(local.Checkpoint, budget);
        _knownBudgetRevisions[profileId] = budget.Revision;
        return local;
    }

    public StoredRecoveryState Save(Guid profileId, long expectedRevision,
        RecoveryCheckpoint checkpoint)
    {
        SharedRecoveryBudget budget = _budgets.Load(profileId);
        if (!_knownBudgetRevisions.TryGetValue(profileId, out long knownRevision) ||
            knownRevision != budget.Revision)
            throw new StaleRecoveryRevisionException(
                "The shared recovery budget changed; this session must reconcile before saving.");
        EnsureSharedBudgetFieldsMatch(checkpoint, budget);
        return _session.Save(profileId, expectedRevision,
            checkpoint with { SharedBudgetRevision = budget.Revision });
    }

    public void AdoptCommittedBudget(SharedRecoveryBudget committed)
    {
        ArgumentNullException.ThrowIfNull(committed);
        if (!_knownBudgetRevisions.TryGetValue(committed.ProfileId, out long knownRevision) ||
            committed.Revision != knownRevision + 1 ||
            _budgets.Load(committed.ProfileId) != committed)
            throw new StaleRecoveryRevisionException(
                "A shared budget commit cannot be adopted by this session.");
        _knownBudgetRevisions[committed.ProfileId] = committed.Revision;
    }

    private static void EnsureSharedBudgetMatches(RecoveryCheckpoint checkpoint,
        SharedRecoveryBudget budget)
    {
        if (checkpoint.SharedBudgetRevision != budget.Revision)
            throw new StaleRecoveryRevisionException(
                "The shared recovery budget revision changed; this session must reconcile.");
        EnsureSharedBudgetFieldsMatch(checkpoint, budget);
    }

    private static void EnsureSharedBudgetFieldsMatch(RecoveryCheckpoint checkpoint,
        SharedRecoveryBudget budget)
    {
        if (checkpoint.ReservedAutomaticAttempts != budget.ReservedAutomaticAttempts ||
            checkpoint.EpisodeId != budget.EpisodeId ||
            budget.LockedOut && !checkpoint.LockedOut)
            throw new StaleRecoveryRevisionException(
                "The shared recovery budget changed; this session must reconcile before automatic actions.");
    }
}

using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using System.Text.Json;
using Relight.Core;

namespace Relight.Storage;

/// <summary>
/// Stores live recovery state under an opaque logon-session key. It refuses to
/// read or write a session checkpoint if the shared budget is missing or has
/// moved ahead. The coordinator must commit shared budget changes first.
/// </summary>
public sealed class RecoverySessionStateStore : IRecoveryStateStore
{
    private const string MarkerContents = "Relight session checkpoint v1\n";
    private static readonly Regex StorageKeyPattern = new("^[0-9A-F]{32}$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private readonly RecoveryStateStore _session;
    private readonly string _markerDirectory;
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
        string sessionDirectory = Path.Combine(Path.GetFullPath(dataDirectory),
            "Sessions", storageKey);
        _session = new RecoveryStateStore(sessionDirectory);
        _markerDirectory = Path.Combine(sessionDirectory, "State");
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
        CreateMarker(profileId);
        StoredRecoveryState created = _session.Create(profileId, initial);
        _knownBudgetRevisions[profileId] = budget.Revision;
        return created;
    }

    public bool HasStateEvidence(Guid profileId) =>
        _session.HasStateEvidence(profileId) || File.Exists(MarkerPath(profileId));

    public StoredRecoveryState Create(Guid profileId, RecoveryCheckpoint initial)
    {
        SharedRecoveryBudget budget = _budgets.Load(profileId);
        EnsureSharedBudgetFieldsMatch(initial, budget);
        CreateMarker(profileId);
        StoredRecoveryState created = _session.Create(profileId,
            initial with { SharedBudgetRevision = budget.Revision });
        _knownBudgetRevisions[profileId] = budget.Revision;
        return created;
    }

    /// <summary>
    /// Explicitly repairs only this sign-in's untrusted checkpoint. The shared
    /// budget remains authoritative, including lockout and charged attempts.
    /// The replacement starts paused, so a later user action is required to
    /// resume monitoring policy after discovery.
    /// </summary>
    public StoredRecoveryState RepairUnavailableCheckpointExplicitly(Guid profileId,
        bool enabled)
    {
        SharedRecoveryBudget budget = _budgets.Load(profileId);
        if (budget.PendingAutomaticOperationId is not null ||
            budget.PendingExplicitOperationId is not null)
            throw new RecoveryStateUnavailableException(
                "An unresolved launch prevents checkpoint repair.");
        if (!File.Exists(MarkerPath(profileId)))
            throw new RecoveryStateUnavailableException(
                "This sign-in has no session ownership evidence to repair.");
        EnsureMarker(profileId);
        try
        {
            _session.Load(profileId);
            throw new InvalidOperationException(
                "The session checkpoint is readable; reconcile its budget instead of replacing it.");
        }
        catch (RecoveryStateUnavailableException)
        {
            // Missing or corrupt session bytes require this explicit repair.
        }
        RecoveryState state = !enabled ? RecoveryState.Disabled :
            budget.LockedOut ? RecoveryState.AwaitingIntervention :
            RecoveryState.WaitingForFirstStart;
        var replacement = new RecoveryCheckpoint(enabled, true, false,
            budget.LockedOut, budget.ReservedAutomaticAttempts, budget.EpisodeId,
            state, null, SharedBudgetRevision: budget.Revision);
        StoredRecoveryState repaired = _session.ReplaceUntrustedAfterExplicitRepair(
            profileId, replacement);
        if (_budgets.Load(profileId) != budget)
            throw new StaleRecoveryRevisionException(
                "The shared budget changed during checkpoint repair; automatic actions remain suspended.");
        _knownBudgetRevisions[profileId] = budget.Revision;
        return repaired;
    }

    public bool CanRepairUnavailableCheckpoint(Guid profileId)
    {
        try
        {
            SharedRecoveryBudget budget = _budgets.Load(profileId);
            if (budget.PendingAutomaticOperationId is not null ||
                budget.PendingExplicitOperationId is not null ||
                !File.Exists(MarkerPath(profileId)))
                return false;
            EnsureMarker(profileId);
            try { _session.Load(profileId); return false; }
            catch (RecoveryStateUnavailableException error)
            {
                return error.InnerException is null or JsonException;
            }
        }
        catch (Exception error) when (error is IOException or InvalidOperationException or
            ArgumentException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    public StoredRecoveryState Load(Guid profileId)
    {
        SharedRecoveryBudget budget = _budgets.Load(profileId);
        StoredRecoveryState local = _session.Load(profileId);
        EnsureSharedBudgetMatches(local.Checkpoint, budget);
        EnsureMarker(profileId);
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

    private string MarkerPath(Guid profileId)
    {
        if (profileId == Guid.Empty)
            throw new ArgumentException("Profile ID is required.", nameof(profileId));
        return Path.Combine(_markerDirectory, $"{profileId:N}.session");
    }

    private void CreateMarker(Guid profileId)
    {
        string path = MarkerPath(profileId);
        if (File.Exists(path))
            throw new RecoveryStateUnavailableException(
                "This logon session already owns recovery state for the profile; missing state cannot be reinitialized.");
        try
        {
            using var stream = new FileStream(path, FileMode.CreateNew,
                FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough);
            byte[] bytes = System.Text.Encoding.UTF8.GetBytes(MarkerContents);
            stream.Write(bytes);
            stream.Flush(flushToDisk: true);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            throw new RecoveryStateUnavailableException(
                "Session ownership could not be committed; automatic actions remain suspended.", error);
        }
    }

    private void EnsureMarker(Guid profileId)
    {
        string path = MarkerPath(profileId);
        if (!File.Exists(path)) CreateMarker(profileId);
        try
        {
            if (File.ReadAllText(path) != MarkerContents)
                throw new RecoveryStateUnavailableException(
                    "Session ownership marker is invalid; automatic actions remain suspended.");
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            throw new RecoveryStateUnavailableException(
                "Session ownership marker cannot be read; automatic actions remain suspended.", error);
        }
    }
}

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Relight.Core;

namespace Relight.Storage;

public sealed class RecoveryStateUnavailableException(string message, Exception? inner = null)
    : IOException(message, inner);

public sealed class StaleRecoveryRevisionException(string message) : InvalidOperationException(message);

public sealed record StoredRecoveryState(Guid ProfileId, long Revision, RecoveryCheckpoint Checkpoint);

public enum LegacyStateOwnership { Unclaimed, MigrationPending, SessionOwner }

internal enum MigrationBoundary
{
    OwnershipMarked,
    LegacyArchived,
    TombstoneWritten,
    BudgetImported,
    SessionCreated
}

public interface IRecoveryStateStore
{
    StoredRecoveryState Create(Guid profileId, RecoveryCheckpoint initial);
    StoredRecoveryState Load(Guid profileId);
    StoredRecoveryState Save(Guid profileId, long expectedRevision, RecoveryCheckpoint checkpoint);
}

/// <summary>
/// Per-profile, versioned, revision-checked snapshots. A caller must commit a
/// reserved attempt here successfully before sending a launch request to Windows.
/// </summary>
public sealed class RecoveryStateStore : IRecoveryStateStore
{
    private const int SchemaVersion = 1;
    private const string LegacyTombstone = "Relight recovery state migrated; legacy access disabled v1\n";
    private static readonly JsonSerializerOptions Json = CreateJsonOptions();
    private readonly string _directory;

    public RecoveryStateStore(string dataDirectory)
    {
        if (string.IsNullOrWhiteSpace(dataDirectory))
            throw new ArgumentException("Data directory is required.", nameof(dataDirectory));
        _directory = Path.Combine(Path.GetFullPath(dataDirectory), "State");
        Directory.CreateDirectory(_directory);
    }

    public StoredRecoveryState Create(Guid profileId, RecoveryCheckpoint initial)
    {
        CheckId(profileId);
        using FileStream guard = Lock(profileId);
        RejectMigratedProfile(profileId);
        string path = PathFor(profileId);
        if (File.Exists(path))
            throw new InvalidOperationException("Recovery state already exists for this profile.");
        var state = new StoredRecoveryState(profileId, 1, initial);
        Write(path, state, initialCreate: true);
        return state;
    }

    public StoredRecoveryState Load(Guid profileId)
    {
        CheckId(profileId);
        using FileStream guard = Lock(profileId);
        RejectMigratedProfile(profileId);
        return Read(PathFor(profileId), profileId);
    }

    public StoredRecoveryState Save(Guid profileId, long expectedRevision, RecoveryCheckpoint checkpoint)
    {
        CheckId(profileId);
        using FileStream guard = Lock(profileId);
        RejectMigratedProfile(profileId);
        string path = PathFor(profileId);
        StoredRecoveryState prior = Read(path, profileId);
        if (prior.Revision != expectedRevision)
            throw new StaleRecoveryRevisionException(
                $"Profile {profileId} has revision {prior.Revision}, expected {expectedRevision}.");
        if (expectedRevision == long.MaxValue)
            throw new RecoveryStateUnavailableException("Recovery revision counter exhausted.");
        var next = new StoredRecoveryState(profileId, expectedRevision + 1, checkpoint);
        Write(path, next, initialCreate: false);
        return next;
    }

    internal StoredRecoveryState ReplaceUntrustedAfterExplicitRepair(Guid profileId,
        RecoveryCheckpoint checkpoint)
    {
        CheckId(profileId);
        using FileStream guard = Lock(profileId);
        RejectMigratedProfile(profileId);
        string path = PathFor(profileId);
        try
        {
            Read(path, profileId);
            throw new InvalidOperationException(
                "Trusted recovery state must be updated through its revision, not repaired.");
        }
        catch (RecoveryStateUnavailableException)
        {
            // The caller explicitly requested repair. Preserve every existing
            // byte before replacing the unreadable checkpoint.
        }
        string evidence = Path.Combine(_directory, "RepairEvidence", profileId.ToString("N"),
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(evidence);
        CopyEvidence(path, Path.Combine(evidence, "state.json"));
        CopyEvidence(path + ".bak", Path.Combine(evidence, "state.json.bak"));
        var replacement = new StoredRecoveryState(profileId, 1, checkpoint);
        Write(path, replacement, initialCreate: !File.Exists(path));
        return replacement;
    }

    private static void CopyEvidence(string source, string destination)
    {
        if (!File.Exists(source)) return;
        try
        {
            using var input = new FileStream(source, FileMode.Open, FileAccess.Read,
                FileShare.Read);
            using var output = new FileStream(destination, FileMode.CreateNew,
                FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough);
            input.CopyTo(output);
            output.Flush(flushToDisk: true);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            throw new RecoveryStateUnavailableException(
                "Untrusted recovery evidence could not be preserved; repair remains suspended.",
                error);
        }
    }

    public bool HasStateEvidence(Guid profileId)
    {
        CheckId(profileId);
        using FileStream guard = Lock(profileId);
        string path = PathFor(profileId);
        return File.Exists(path) || File.Exists(path + ".bak") ||
            File.Exists(ArchivedPathFor(profileId)) ||
            File.Exists(OwnershipPathFor(profileId));
    }

    public LegacyStateOwnership GetOwnership(Guid profileId)
    {
        CheckId(profileId);
        using FileStream guard = Lock(profileId);
        return GetOwnershipWithoutLock(profileId);
    }

    /// <summary>
    /// Starts ownership transfer before creating either new record. A failed or
    /// interrupted transfer leaves the marker in place so upgraded legacy
    /// writers cannot resume using the old snapshot as launch authority.
    /// </summary>
    public void MigrateToSession(Guid profileId, SharedRecoveryBudgetStore budgets,
        RecoverySessionStateStore session)
        => MigrateToSession(profileId, budgets, session, null);

    internal void MigrateToSession(Guid profileId, SharedRecoveryBudgetStore budgets,
        RecoverySessionStateStore session, Action<MigrationBoundary>? afterBoundary)
    {
        ArgumentNullException.ThrowIfNull(budgets);
        ArgumentNullException.ThrowIfNull(session);
        CheckId(profileId);
        using FileStream guard = Lock(profileId);
        RejectMigratedProfile(profileId);
        StoredRecoveryState legacy = Read(PathFor(profileId), profileId);
        if (legacy.Checkpoint.SharedBudgetRevision is not null)
            throw new RecoveryStateUnavailableException(
                "The legacy snapshot already references a shared budget.");
        if (File.Exists(ArchivedPathFor(profileId)))
            throw new RecoveryStateUnavailableException(
                "Legacy recovery evidence already has an archive; migration is suspended.");

        WriteOwnershipMarker(profileId, "migration-pending");
        afterBoundary?.Invoke(MigrationBoundary.OwnershipMarked);
        ArchiveLegacyState(profileId);
        afterBoundary?.Invoke(MigrationBoundary.LegacyArchived);
        EnsureLegacyTombstone(profileId);
        afterBoundary?.Invoke(MigrationBoundary.TombstoneWritten);
        SharedRecoveryBudget budget = budgets.ImportLegacy(legacy);
        afterBoundary?.Invoke(MigrationBoundary.BudgetImported);
        RecoveryCheckpoint old = legacy.Checkpoint;
        RecoveryState initialState = !old.Enabled ? RecoveryState.Disabled :
            budget.LockedOut ? RecoveryState.AwaitingIntervention :
            RecoveryState.WaitingForFirstStart;
        session.Create(profileId, new RecoveryCheckpoint(old.Enabled, old.Paused,
            false, budget.LockedOut, budget.ReservedAutomaticAttempts,
            budget.EpisodeId, initialState, null,
            SharedBudgetRevision: budget.Revision));
        afterBoundary?.Invoke(MigrationBoundary.SessionCreated);
        WriteOwnershipMarker(profileId, "session-owner");
    }

    /// <summary>
    /// Completes only a transfer whose imported budget is still byte-for-byte
    /// equivalent at the model level to the untouched legacy snapshot. A missing
    /// budget cannot be recreated: it may have contained later reservations.
    /// </summary>
    public void RepairPendingMigration(Guid profileId, SharedRecoveryBudgetStore budgets,
        RecoverySessionStateStore session)
    {
        ArgumentNullException.ThrowIfNull(budgets);
        ArgumentNullException.ThrowIfNull(session);
        CheckId(profileId);
        using FileStream guard = Lock(profileId);
        if (GetOwnershipWithoutLock(profileId) != LegacyStateOwnership.MigrationPending)
            throw new InvalidOperationException("This profile has no pending migration to repair.");

        if (!File.Exists(ArchivedPathFor(profileId)))
        {
            // A tombstone without its archive is missing the only trusted
            // legacy budget evidence. Never archive the tombstone itself.
            Read(PathFor(profileId), profileId);
            ArchiveLegacyState(profileId);
        }
        EnsureLegacyTombstone(profileId);
        StoredRecoveryState legacy = Read(ArchivedPathFor(profileId), profileId);
        SharedRecoveryBudget expected = SharedRecoveryBudgetStore.ProjectLegacy(legacy);
        SharedRecoveryBudget actual = budgets.Load(profileId);
        if (actual != expected)
            throw new RecoveryStateUnavailableException(
                "The imported budget differs from legacy evidence; migration repair remains suspended.");

        RecoveryCheckpoint old = legacy.Checkpoint;
        RecoveryState initialState = !old.Enabled ? RecoveryState.Disabled :
            actual.LockedOut ? RecoveryState.AwaitingIntervention :
            RecoveryState.WaitingForFirstStart;
        var expectedCheckpoint = new RecoveryCheckpoint(old.Enabled, old.Paused,
            false, actual.LockedOut, actual.ReservedAutomaticAttempts,
            actual.EpisodeId, initialState, null,
            SharedBudgetRevision: actual.Revision);
        if (session.HasStateEvidence(profileId))
        {
            if (session.Load(profileId).Checkpoint != expectedCheckpoint)
                throw new RecoveryStateUnavailableException(
                    "The session checkpoint differs from legacy evidence; migration repair remains suspended.");
        }
        else
        {
            session.Create(profileId, expectedCheckpoint);
        }
        WriteOwnershipMarker(profileId, "session-owner");
    }

    private void RejectMigratedProfile(Guid profileId)
    {
        if (File.Exists(OwnershipPathFor(profileId)))
            throw new RecoveryStateUnavailableException(
                $"Legacy recovery state for profile {profileId} has transferred ownership; it cannot authorize launches.");
    }

    private void ArchiveLegacyState(Guid profileId)
    {
        string original = PathFor(profileId);
        string archived = ArchivedPathFor(profileId);
        if (File.Exists(archived))
            throw new RecoveryStateUnavailableException(
                "Legacy recovery evidence conflicts with its archive; migration is suspended.");
        try
        {
            // The legacy file lock is held by the caller. Published older
            // writers need this original path for every reservation, so a
            // same-volume move makes their next write fail closed.
            File.Move(original, archived);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            throw new RecoveryStateUnavailableException(
                "Legacy recovery state could not be archived; migration is suspended.", error);
        }
    }

    private void EnsureLegacyTombstone(Guid profileId)
    {
        string path = PathFor(profileId);
        try
        {
            if (File.Exists(path))
            {
                if (File.ReadAllText(path) != LegacyTombstone)
                    throw new RecoveryStateUnavailableException(
                        "Legacy state reappeared after archival; migration is suspended.");
                return;
            }
            using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write,
                FileShare.None, 4096, FileOptions.WriteThrough);
            byte[] bytes = Encoding.UTF8.GetBytes(LegacyTombstone);
            stream.Write(bytes);
            stream.Flush(flushToDisk: true);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            throw new RecoveryStateUnavailableException(
                "Legacy recovery tombstone could not be verified; migration is suspended.", error);
        }
    }

    private LegacyStateOwnership GetOwnershipWithoutLock(Guid profileId)
    {
        string path = OwnershipPathFor(profileId);
        if (!File.Exists(path)) return LegacyStateOwnership.Unclaimed;
        try
        {
            return File.ReadAllText(path).Trim() switch
            {
                "Relight recovery-state migration-pending v1" => LegacyStateOwnership.MigrationPending,
                "Relight recovery-state session-owner v1" => LegacyStateOwnership.SessionOwner,
                _ => throw new RecoveryStateUnavailableException(
                    "Recovery-state ownership marker is invalid; automatic actions remain suspended.")
            };
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            throw new RecoveryStateUnavailableException(
                "Recovery-state ownership marker cannot be read; automatic actions remain suspended.", error);
        }
    }

    private void WriteOwnershipMarker(Guid profileId, string phase)
    {
        string path = OwnershipPathFor(profileId);
        string temporary = Path.Combine(_directory,
            $"{profileId:N}.{Guid.NewGuid():N}.owner.tmp");
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew,
                       FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            {
                byte[] payload = Encoding.UTF8.GetBytes($"Relight recovery-state {phase} v1\n");
                stream.Write(payload);
                stream.Flush(flushToDisk: true);
            }
            if (phase == "migration-pending") File.Move(temporary, path);
            else File.Replace(temporary, path, null, ignoreMetadataErrors: false);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            throw new RecoveryStateUnavailableException(
                "Recovery-state ownership marker could not be committed; migration is suspended.", error);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    private FileStream Lock(Guid profileId)
    {
        try
        {
            // Held across read, compare and replace; two Windows sessions cannot
            // both advance the same profile revision through this repository.
            return new FileStream(PathFor(profileId) + ".lock", FileMode.OpenOrCreate,
                FileAccess.ReadWrite, FileShare.None);
        }
        catch (IOException error)
        {
            throw new RecoveryStateUnavailableException(
                $"Recovery state for profile {profileId} is busy or inaccessible.", error);
        }
    }

    private StoredRecoveryState Read(string path, Guid profileId)
    {
        if (!File.Exists(path))
            throw new RecoveryStateUnavailableException(
                $"Recovery state is missing for existing profile {profileId}. Automatic launches must remain suspended.");
        try
        {
            byte[] bytes = File.ReadAllBytes(path);
            var record = JsonSerializer.Deserialize<StateFile>(bytes, Json)
                ?? throw new JsonException("State file is empty.");
            if (record.SchemaVersion != SchemaVersion || record.ProfileId != profileId ||
                record.Revision < 1 || record.Checkpoint is null ||
                !Enum.IsDefined(record.Checkpoint.LastState) ||
                (record.Checkpoint.HoldReason is { } holdReason && !Enum.IsDefined(holdReason)) ||
                record.Checkpoint.ReservedAutomaticAttempts is < 0 or > 20 ||
                record.Checkpoint.SharedBudgetRevision is < 1 ||
                (record.Checkpoint.LastState == RecoveryState.Starting &&
                 record.Checkpoint.PendingOperationId is null) ||
                (record.Checkpoint.PendingExplicitStart == true &&
                 record.Checkpoint.LastState != RecoveryState.Starting))
                throw new JsonException("State schema or contents are invalid.");

            string expected = Checksum(record.ProfileId, record.Revision, record.Checkpoint);
            if (!string.Equals(expected, record.Checksum, StringComparison.OrdinalIgnoreCase))
                throw new JsonException("State checksum does not match.");
            return new(profileId, record.Revision, record.Checkpoint);
        }
        catch (Exception error) when (error is JsonException or IOException or UnauthorizedAccessException)
        {
            // Preserve the original evidence. Never substitute the backup automatically;
            // doing so could refund an attempt reserved after that backup was made.
            throw new RecoveryStateUnavailableException(
                $"Recovery state for profile {profileId} cannot be trusted. Automatic launches must remain suspended.",
                error);
        }
    }

    private void Write(string path, StoredRecoveryState state, bool initialCreate)
    {
        string temporary = Path.Combine(_directory, $"{state.ProfileId:N}.{Guid.NewGuid():N}.tmp");
        var file = new StateFile(SchemaVersion, state.ProfileId, state.Revision,
            state.Checkpoint, Checksum(state.ProfileId, state.Revision, state.Checkpoint));
        try
        {
            byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(file, Json);
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write,
                       FileShare.None, 4096, FileOptions.WriteThrough))
            {
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }
            if (initialCreate)
                File.Move(temporary, path);
            else
                File.Replace(temporary, path, path + ".bak", ignoreMetadataErrors: false);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            throw new RecoveryStateUnavailableException(
                "Recovery state could not be committed; automatic launch must not dispatch.", error);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    private string PathFor(Guid profileId) => Path.Combine(_directory, profileId.ToString("N") + ".json");

    private string ArchivedPathFor(Guid profileId) => Path.Combine(_directory,
        profileId.ToString("N") + ".legacy.json");

    private string OwnershipPathFor(Guid profileId) => Path.Combine(_directory,
        profileId.ToString("N") + ".owner");

    private static string Checksum(Guid profileId, long revision, RecoveryCheckpoint checkpoint)
    {
        byte[] payload = JsonSerializer.SerializeToUtf8Bytes(
            new ChecksumPayload(profileId, revision, checkpoint), Json);
        return Convert.ToHexString(SHA256.HashData(payload));
    }

    private static JsonSerializerOptions CreateJsonOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            WriteIndented = true,
            RespectRequiredConstructorParameters = true
        };
        options.Converters.Add(new JsonStringEnumConverter(allowIntegerValues: false));
        return options;
    }

    private static void CheckId(Guid profileId)
    {
        if (profileId == Guid.Empty) throw new ArgumentException("Profile ID is required.", nameof(profileId));
    }

    private sealed record StateFile(
        int SchemaVersion,
        Guid ProfileId,
        long Revision,
        RecoveryCheckpoint Checkpoint,
        string Checksum);

    private sealed record ChecksumPayload(Guid ProfileId, long Revision, RecoveryCheckpoint Checkpoint);
}

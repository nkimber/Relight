using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Relight.Core;

namespace Relight.Storage;

/// <summary>
/// Shared across logon sessions. A reservation is committed before a launch is
/// dispatched; pending operations and lockout cannot be reconstructed from a
/// session's process observations alone.
/// </summary>
public sealed record SharedRecoveryBudget(
    Guid ProfileId,
    long Revision,
    Guid? EpisodeId,
    int ReservedAutomaticAttempts,
    bool LockedOut,
    Guid? PendingAutomaticOperationId,
    Guid? PendingExplicitOperationId);

public sealed class SharedRecoveryBudgetStore
{
    private const int SchemaVersion = 1;
    private static readonly JsonSerializerOptions Json = CreateJsonOptions();
    private readonly string _directory;

    public SharedRecoveryBudgetStore(string dataDirectory)
    {
        if (string.IsNullOrWhiteSpace(dataDirectory))
            throw new ArgumentException("Data directory is required.", nameof(dataDirectory));
        _directory = Path.Combine(Path.GetFullPath(dataDirectory), "Budgets");
        Directory.CreateDirectory(_directory);
    }

    public SharedRecoveryBudget Create(Guid profileId)
    {
        CheckId(profileId);
        var initial = new SharedRecoveryBudget(profileId, 1, null, 0, false, null, null);
        return CreateInitial(initial);
    }

    public SharedRecoveryBudget ImportLegacy(StoredRecoveryState legacy)
    {
        ArgumentNullException.ThrowIfNull(legacy);
        CheckId(legacy.ProfileId);
        RecoveryCheckpoint checkpoint = legacy.Checkpoint;
        if (legacy.Revision < 1 || checkpoint.ReservedAutomaticAttempts is < 0 or > 20 ||
            checkpoint.ReservedAutomaticAttempts > 0 && checkpoint.EpisodeId is null ||
            checkpoint.EpisodeId == Guid.Empty ||
            checkpoint.PendingOperationId == Guid.Empty ||
            checkpoint.LastState == RecoveryState.Starting &&
            checkpoint.PendingOperationId is null)
            throw new RecoveryStateUnavailableException(
                "Legacy recovery state cannot be migrated without losing budget evidence.");
        bool explicitPending = checkpoint.PendingExplicitStart == true;
        var initial = new SharedRecoveryBudget(legacy.ProfileId, 1,
            checkpoint.EpisodeId, checkpoint.ReservedAutomaticAttempts,
            checkpoint.LockedOut,
            explicitPending ? null : checkpoint.PendingOperationId,
            explicitPending ? checkpoint.PendingOperationId : null);
        return CreateInitial(initial);
    }

    public SharedRecoveryBudget Load(Guid profileId)
    {
        CheckId(profileId);
        using FileStream guard = Lock(profileId);
        return Read(PathFor(profileId), profileId);
    }

    public bool HasBudgetEvidence(Guid profileId)
    {
        CheckId(profileId);
        using FileStream guard = Lock(profileId);
        string path = PathFor(profileId);
        return File.Exists(path) || File.Exists(path + ".bak");
    }

    public SharedRecoveryBudget BeginEpisode(Guid profileId, long expectedRevision,
        Guid episodeId)
    {
        CheckId(episodeId);
        return Update(profileId, expectedRevision, current =>
        {
            if (current.EpisodeId is not null || current.ReservedAutomaticAttempts != 0 ||
                current.LockedOut || current.PendingAutomaticOperationId is not null ||
                current.PendingExplicitOperationId is not null)
                throw new InvalidOperationException(
                    "The shared recovery budget cannot begin another episode.");
            return current with { EpisodeId = episodeId };
        });
    }

    public SharedRecoveryBudget ReserveAutomatic(Guid profileId, long expectedRevision,
        int attemptLimit, Guid episodeId, Guid operationId)
    {
        if (attemptLimit is < 0 or > 20) throw new ArgumentOutOfRangeException(nameof(attemptLimit));
        CheckId(episodeId);
        CheckId(operationId);
        return Update(profileId, expectedRevision, current =>
        {
            if (current.LockedOut || current.ReservedAutomaticAttempts >= attemptLimit ||
                current.PendingAutomaticOperationId is not null ||
                current.PendingExplicitOperationId is not null)
                throw new InvalidOperationException("The shared recovery budget cannot authorize a launch.");
            if (current.EpisodeId is { } existing && existing != episodeId)
                throw new StaleRecoveryRevisionException("The recovery episode changed in another session.");
            return current with
            {
                EpisodeId = episodeId,
                ReservedAutomaticAttempts = current.ReservedAutomaticAttempts + 1,
                PendingAutomaticOperationId = operationId
            };
        });
    }

    public SharedRecoveryBudget ResolveAutomatic(Guid profileId, long expectedRevision,
        Guid operationId)
    {
        CheckId(operationId);
        return Update(profileId, expectedRevision, current =>
        {
            if (current.PendingAutomaticOperationId != operationId)
                throw new StaleRecoveryRevisionException("The pending automatic operation changed.");
            return current with { PendingAutomaticOperationId = null };
        });
    }

    public SharedRecoveryBudget EnterLockout(Guid profileId, long expectedRevision,
        Guid episodeId)
    {
        CheckId(episodeId);
        return Update(profileId, expectedRevision, current =>
        {
            if (current.EpisodeId != episodeId || current.PendingAutomaticOperationId is not null)
                throw new StaleRecoveryRevisionException("The recovery episode is not ready for lockout.");
            return current with { LockedOut = true };
        });
    }

    public SharedRecoveryBudget MarkExplicitStart(Guid profileId, long expectedRevision,
        Guid operationId)
    {
        CheckId(operationId);
        return Update(profileId, expectedRevision, current =>
        {
            if (current.PendingAutomaticOperationId is not null ||
                current.PendingExplicitOperationId is not null)
                throw new InvalidOperationException("Another launch is already pending for this profile.");
            return current with { PendingExplicitOperationId = operationId };
        });
    }

    public SharedRecoveryBudget ResolveExplicitStart(Guid profileId, long expectedRevision,
        Guid operationId)
    {
        CheckId(operationId);
        return Update(profileId, expectedRevision, current =>
        {
            if (current.PendingExplicitOperationId != operationId)
                throw new StaleRecoveryRevisionException("The pending explicit operation changed.");
            return current with { PendingExplicitOperationId = null };
        });
    }

    public SharedRecoveryBudget CompleteStableObservation(Guid profileId,
        long expectedRevision, Guid episodeId)
    {
        CheckId(episodeId);
        return Update(profileId, expectedRevision, current =>
        {
            if (current.EpisodeId != episodeId)
                throw new StaleRecoveryRevisionException("The recovery episode changed in another session.");
            if (current.PendingAutomaticOperationId is not null ||
                current.PendingExplicitOperationId is not null)
                throw new InvalidOperationException(
                    "A launch is unresolved; stable observation cannot clear it.");
            return current with
            {
                EpisodeId = null,
                ReservedAutomaticAttempts = 0,
                LockedOut = false,
                PendingAutomaticOperationId = null,
                PendingExplicitOperationId = null
            };
        });
    }

    public SharedRecoveryBudget ResetExplicitly(Guid profileId, long expectedRevision) =>
        Update(profileId, expectedRevision, current =>
        {
            if (current.PendingAutomaticOperationId is not null ||
                current.PendingExplicitOperationId is not null)
                throw new InvalidOperationException(
                    "An unresolved launch must be reconciled before recovery can be reset.");
            return current with
            {
                EpisodeId = null,
                ReservedAutomaticAttempts = 0,
                LockedOut = false
            };
        });

    private SharedRecoveryBudget Update(Guid profileId, long expectedRevision,
        Func<SharedRecoveryBudget, SharedRecoveryBudget> change)
    {
        CheckId(profileId);
        using FileStream guard = Lock(profileId);
        string path = PathFor(profileId);
        SharedRecoveryBudget prior = Read(path, profileId);
        if (prior.Revision != expectedRevision)
            throw new StaleRecoveryRevisionException(
                $"Shared budget revision is {prior.Revision}, expected {expectedRevision}.");
        if (prior.Revision == long.MaxValue)
            throw new RecoveryStateUnavailableException("Shared budget revision counter exhausted.");
        SharedRecoveryBudget next = change(prior) with { Revision = prior.Revision + 1 };
        Write(path, next, initialCreate: false);
        return next;
    }

    private SharedRecoveryBudget CreateInitial(SharedRecoveryBudget initial)
    {
        using FileStream guard = Lock(initial.ProfileId);
        string path = PathFor(initial.ProfileId);
        if (File.Exists(path) || File.Exists(path + ".bak"))
            throw new InvalidOperationException(
                "A shared budget or its backup already exists for this profile.");
        Write(path, initial, initialCreate: true);
        return initial;
    }

    private FileStream Lock(Guid profileId)
    {
        try
        {
            return new FileStream(PathFor(profileId) + ".lock", FileMode.OpenOrCreate,
                FileAccess.ReadWrite, FileShare.None);
        }
        catch (IOException error)
        {
            throw new RecoveryStateUnavailableException(
                $"Shared budget for profile {profileId} is busy or inaccessible.", error);
        }
    }

    private SharedRecoveryBudget Read(string path, Guid profileId)
    {
        if (!File.Exists(path))
            throw new RecoveryStateUnavailableException(
                $"Shared budget is missing for existing profile {profileId}; automatic launches must remain suspended.");
        try
        {
            byte[] bytes = File.ReadAllBytes(path);
            BudgetFile file = JsonSerializer.Deserialize<BudgetFile>(bytes, Json)
                ?? throw new JsonException("Shared budget file is empty.");
            SharedRecoveryBudget state = file.Budget;
            if (file.SchemaVersion != SchemaVersion || state is null ||
                state.ProfileId != profileId || state.Revision < 1 ||
                state.ReservedAutomaticAttempts is < 0 or > 20 ||
                state.EpisodeId == Guid.Empty ||
                state.PendingAutomaticOperationId == Guid.Empty ||
                state.PendingExplicitOperationId == Guid.Empty ||
                state.PendingAutomaticOperationId is not null &&
                state.PendingExplicitOperationId is not null ||
                state.ReservedAutomaticAttempts > 0 && state.EpisodeId is null)
                throw new JsonException("Shared budget schema or contents are invalid.");
            if (!string.Equals(file.Checksum, Checksum(state),
                    StringComparison.OrdinalIgnoreCase))
                throw new JsonException("Shared budget checksum does not match.");
            return state;
        }
        catch (Exception error) when (error is JsonException or IOException or UnauthorizedAccessException)
        {
            throw new RecoveryStateUnavailableException(
                $"Shared budget for profile {profileId} cannot be trusted; automatic launches must remain suspended.",
                error);
        }
    }

    private void Write(string path, SharedRecoveryBudget budget, bool initialCreate)
    {
        string temporary = Path.Combine(_directory,
            $"{budget.ProfileId:N}.{Guid.NewGuid():N}.tmp");
        try
        {
            byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(
                new BudgetFile(SchemaVersion, budget, Checksum(budget)), Json);
            using (var stream = new FileStream(temporary, FileMode.CreateNew,
                       FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            {
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }
            if (initialCreate) File.Move(temporary, path);
            else File.Replace(temporary, path, path + ".bak", ignoreMetadataErrors: false);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            throw new RecoveryStateUnavailableException(
                "Shared budget could not be committed; automatic launch must not dispatch.", error);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    private string PathFor(Guid profileId) => Path.Combine(_directory,
        profileId.ToString("N") + ".json");

    private static string Checksum(SharedRecoveryBudget budget) =>
        Convert.ToHexString(SHA256.HashData(
            JsonSerializer.SerializeToUtf8Bytes(budget, Json)));

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

    private static void CheckId(Guid id)
    {
        if (id == Guid.Empty) throw new ArgumentException("An ID is required.", nameof(id));
    }

    private sealed record BudgetFile(int SchemaVersion,
        SharedRecoveryBudget Budget, string Checksum);
}

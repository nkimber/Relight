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

/// <summary>
/// Per-profile, versioned, revision-checked snapshots. A caller must commit a
/// reserved attempt here successfully before sending a launch request to Windows.
/// </summary>
public sealed class RecoveryStateStore
{
    private const int SchemaVersion = 1;
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
        return Read(PathFor(profileId), profileId);
    }

    public StoredRecoveryState Save(Guid profileId, long expectedRevision, RecoveryCheckpoint checkpoint)
    {
        CheckId(profileId);
        using FileStream guard = Lock(profileId);
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
                record.Checkpoint.ReservedAutomaticAttempts is < 0 or > 20 ||
                (record.Checkpoint.LastState == RecoveryState.Starting &&
                 record.Checkpoint.PendingOperationId is null))
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

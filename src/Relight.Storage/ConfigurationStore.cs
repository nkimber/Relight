using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Relight.Core;

namespace Relight.Storage;

public enum TargetKind { Executable, PackagedApplication }
public enum RelightTheme { System, Light, Dark }

public sealed record TargetConfiguration(
    TargetKind Kind,
    string Identity,
    List<string> Arguments,
    string? WorkingDirectory = null,
    string? RequiredArgument = null,
    string? ExcludedArgument = null);

public sealed record ProfileConfiguration(
    Guid Id,
    string Name,
    bool Enabled,
    TargetConfiguration Target,
    RecoveryPolicy Policy,
    bool NotifyOnRecovery = true,
    bool NotifyOnLockout = true);

public sealed record GlobalConfiguration(
    bool StartAtSignIn,
    RelightTheme Theme,
    int LogRetentionDays,
    long MaximumLogBytes,
    long LogRotationBytes)
{
    public static GlobalConfiguration Default { get; } =
        new(false, RelightTheme.System, 30, 250L * 1024 * 1024, 10L * 1024 * 1024);
}

public sealed record RelightConfiguration(
    List<ProfileConfiguration> Profiles,
    GlobalConfiguration Settings)
{
    public static RelightConfiguration Empty { get; } = new([], GlobalConfiguration.Default);
}

public sealed record StoredConfiguration(
    long Revision,
    string ContentHash,
    RelightConfiguration Configuration,
    bool FromLastGoodBackup,
    string? Diagnostic)
{
    public bool AutomaticActionsAllowed => !FromLastGoodBackup;
}

public sealed class ConfigurationUnavailableException(string message, Exception? inner = null)
    : IOException(message, inner);

public sealed class StaleConfigurationException(string message) : InvalidOperationException(message);

/// <summary>
/// Shared, versioned user configuration. A corrupt external edit can be read
/// from the last-good backup, but never enables automatic actions until repair.
/// Runtime attempt state is deliberately stored elsewhere.
/// </summary>
public sealed class ConfigurationStore
{
    private const int SchemaVersion = 1;
    private static readonly JsonSerializerOptions Json = CreateJsonOptions();
    private readonly string _path;

    public ConfigurationStore(string dataDirectory)
    {
        if (string.IsNullOrWhiteSpace(dataDirectory))
            throw new ArgumentException("Data directory is required.", nameof(dataDirectory));
        string directory = Path.GetFullPath(dataDirectory);
        Directory.CreateDirectory(directory);
        _path = Path.Combine(directory, "configuration.json");
    }

    public StoredConfiguration Initialize(RelightConfiguration configuration)
    {
        Validate(configuration);
        using FileStream guard = Lock();
        if (File.Exists(_path) || File.Exists(BackupPath))
            throw new InvalidOperationException("Configuration already exists or requires repair.");
        byte[] bytes = Serialize(1, configuration);
        Write(bytes, initialCreate: true);
        return new(1, Hash(bytes), configuration, false, null);
    }

    public StoredConfiguration Load()
    {
        using FileStream guard = Lock();
        try { return Read(_path, fromBackup: false); }
        catch (Exception error) when (error is ConfigurationUnavailableException)
        {
            try
            {
                StoredConfiguration backup = Read(BackupPath, fromBackup: true);
                return backup with { Diagnostic = "Current configuration is invalid or inaccessible. Last-good settings are shown; automatic actions are suspended until repair." };
            }
            catch (ConfigurationUnavailableException)
            {
                throw new ConfigurationUnavailableException(
                    "Neither current nor last-good configuration can be trusted. Automatic actions are suspended.",
                    error);
            }
        }
    }

    public StoredConfiguration Save(StoredConfiguration expected, RelightConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(expected);
        Validate(configuration);
        if (expected.FromLastGoodBackup)
            throw new ConfigurationUnavailableException("Repair the invalid current configuration before saving.");
        using FileStream guard = Lock();
        StoredConfiguration current = Read(_path, fromBackup: false);
        if (current.Revision != expected.Revision ||
            !string.Equals(current.ContentHash, expected.ContentHash, StringComparison.Ordinal))
            throw new StaleConfigurationException("Configuration changed since it was loaded.");
        if (current.Revision == long.MaxValue)
            throw new ConfigurationUnavailableException("Configuration revision counter exhausted.");
        byte[] bytes = Serialize(current.Revision + 1, configuration);
        Write(bytes, initialCreate: false);
        return new(current.Revision + 1, Hash(bytes), configuration, false, null);
    }

    private string BackupPath => _path + ".bak";

    private FileStream Lock()
    {
        try
        {
            return new FileStream(_path + ".lock", FileMode.OpenOrCreate,
                FileAccess.ReadWrite, FileShare.None);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            throw new ConfigurationUnavailableException("Configuration is busy or inaccessible.", error);
        }
    }

    private static StoredConfiguration Read(string path, bool fromBackup)
    {
        try
        {
            byte[] bytes = File.ReadAllBytes(path);
            ConfigurationFile file = JsonSerializer.Deserialize<ConfigurationFile>(bytes, Json)
                ?? throw new JsonException("Configuration is empty.");
            if (file.SchemaVersion != SchemaVersion || file.Revision < 1)
                throw new JsonException("Configuration schema or revision is unsupported.");
            Validate(file.Configuration);
            return new(file.Revision, Hash(bytes), file.Configuration, fromBackup, null);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or ArgumentException)
        {
            throw new ConfigurationUnavailableException("Configuration is missing, invalid or inaccessible.", error);
        }
    }

    private void Write(byte[] bytes, bool initialCreate)
    {
        string temporary = _path + $".{Guid.NewGuid():N}.tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write,
                       FileShare.None, 4096, FileOptions.WriteThrough))
            {
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }
            if (initialCreate) File.Move(temporary, _path);
            else File.Replace(temporary, _path, BackupPath, ignoreMetadataErrors: false);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            throw new ConfigurationUnavailableException("Configuration could not be committed.", error);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    private static byte[] Serialize(long revision, RelightConfiguration configuration) =>
        JsonSerializer.SerializeToUtf8Bytes(new ConfigurationFile(SchemaVersion, revision, configuration), Json);

    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));

    private static JsonSerializerOptions CreateJsonOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            WriteIndented = true,
            RespectRequiredConstructorParameters = true,
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
        };
        options.Converters.Add(new JsonStringEnumConverter(allowIntegerValues: false));
        return options;
    }

    private static void Validate(RelightConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        if (configuration.Profiles is null || configuration.Settings is null)
            throw new ArgumentException("Profiles and settings are required.");
        if (configuration.Profiles.Count > 500)
            throw new ArgumentException("Too many profiles.");
        if (!Enum.IsDefined(configuration.Settings.Theme) ||
            configuration.Settings.LogRetentionDays is < 1 or > 3650 ||
            configuration.Settings.MaximumLogBytes is < 1_048_576 or > 10L * 1024 * 1024 * 1024 ||
            configuration.Settings.LogRotationBytes < 65_536 ||
            configuration.Settings.LogRotationBytes > configuration.Settings.MaximumLogBytes)
            throw new ArgumentException("Global settings are invalid.");

        var ids = new HashSet<Guid>();
        var enabledTargets = new List<(TargetKind Kind, string Key, string? Required, string? Excluded)>();
        foreach (ProfileConfiguration profile in configuration.Profiles)
        {
            if (profile is null || profile.Id == Guid.Empty || !ids.Add(profile.Id) ||
                string.IsNullOrWhiteSpace(profile.Name) || profile.Name.Length > 100 ||
                profile.Target is null || profile.Policy is null)
                throw new ArgumentException("Profile identity, name, target or policy is invalid.");
            profile.Policy.Validate();
            TargetConfiguration target = profile.Target;
            if (!Enum.IsDefined(target.Kind) || string.IsNullOrWhiteSpace(target.Identity) ||
                target.Identity.Contains('\0') || target.Arguments is null ||
                target.Arguments.Any(argument => argument is null || argument.Contains('\0')) ||
                target.RequiredArgument is { Length: 0 } || target.ExcludedArgument is { Length: 0 } ||
                target.RequiredArgument?.Contains('\0') == true ||
                target.ExcludedArgument?.Contains('\0') == true ||
                target.RequiredArgument is not null &&
                    string.Equals(target.RequiredArgument, target.ExcludedArgument,
                        StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("Target identity or arguments are invalid.");
            string key;
            if (target.Kind == TargetKind.Executable)
            {
                if (!Path.IsPathFullyQualified(target.Identity) ||
                    !string.Equals(Path.GetExtension(target.Identity), ".exe", StringComparison.OrdinalIgnoreCase) ||
                    target.WorkingDirectory is { Length: > 0 } directory && !Path.IsPathFullyQualified(directory))
                    throw new ArgumentException("Executable paths must be fully qualified.");
                key = Path.GetFullPath(target.Identity);
            }
            else
            {
                if (!target.Identity.Contains('!') || target.WorkingDirectory is not null ||
                    target.Arguments.Count > 0 || target.RequiredArgument is not null ||
                    target.ExcludedArgument is not null)
                    throw new ArgumentException("Packaged application identity is invalid.");
                key = target.Identity;
            }
            if (profile.Enabled)
            {
                bool overlaps = enabledTargets.Any(existing =>
                    existing.Kind == target.Kind &&
                    string.Equals(existing.Key, key, StringComparison.OrdinalIgnoreCase) &&
                    !MutuallyExclusive(existing.Required, existing.Excluded,
                        target.RequiredArgument, target.ExcludedArgument));
                if (overlaps)
                    throw new ArgumentException("Enabled profiles have overlapping target identities.");
                enabledTargets.Add((target.Kind, key, target.RequiredArgument, target.ExcludedArgument));
            }
        }
    }

    private static bool MutuallyExclusive(string? firstRequired, string? firstExcluded,
        string? secondRequired, string? secondExcluded) =>
        (firstRequired is not null &&
            string.Equals(firstRequired, secondExcluded, StringComparison.OrdinalIgnoreCase)) ||
        (secondRequired is not null &&
            string.Equals(secondRequired, firstExcluded, StringComparison.OrdinalIgnoreCase));

    private sealed record ConfigurationFile(
        int SchemaVersion,
        long Revision,
        RelightConfiguration Configuration);
}

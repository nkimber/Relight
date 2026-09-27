using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Relight.Storage;

public sealed record DiagnosticBundleResult(int ExportedEvents, int SkippedMalformedLines);

/// <summary>
/// Creates a local, bounded diagnostic bundle. Configuration arguments, working
/// directories, argument selectors and event process identities are omitted.
/// </summary>
public sealed class DiagnosticBundleExporter(string dataDirectory)
{
    private static readonly JsonSerializerOptions Json = CreateJsonOptions(indented: true);
    private static readonly JsonSerializerOptions JsonLines = CreateJsonOptions(indented: false);
    private readonly string _dataDirectory = Path.GetFullPath(dataDirectory);

    public async Task<DiagnosticBundleResult> ExportAsync(string destination,
        StoredConfiguration? configuration, EventRecorderStatus? logging,
        string applicationVersion, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(destination))
            throw new ArgumentException("Choose an export destination.", nameof(destination));
        string output = Path.GetFullPath(destination);
        string relative = Path.GetRelativePath(_dataDirectory, output);
        if (relative != ".." && !relative.StartsWith(".." + Path.DirectorySeparatorChar,
                StringComparison.Ordinal) && !Path.IsPathRooted(relative))
            throw new ArgumentException("Choose a destination outside Relight's data directory.",
                nameof(destination));
        string parent = Path.GetDirectoryName(output)!;
        if (!Directory.Exists(parent))
            throw new DirectoryNotFoundException("The export folder does not exist.");
        EventHistoryResult history = await new OperationalEventHistoryReader(_dataDirectory)
            .ReadAsync(new EventHistoryQuery(Limit: 500), cancellationToken)
            .ConfigureAwait(false);
        string temporary = Path.Combine(parent, $".relight-diagnostics-{Guid.NewGuid():N}.tmp");
        try
        {
            await using (var stream = new FileStream(temporary, FileMode.CreateNew,
                             FileAccess.Write, FileShare.None, 4096,
                             FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                using (var archive = new ZipArchive(stream, ZipArchiveMode.Create,
                           leaveOpen: true))
                {
                    object redactedConfiguration = configuration is null
                        ? new { Available = false }
                        : new
                        {
                            Available = true,
                            configuration.Revision,
                            configuration.FromLastGoodBackup,
                            Settings = configuration.Configuration.Settings,
                            Profiles = configuration.Configuration.Profiles.Select(profile => new
                            {
                                profile.Id,
                                profile.Name,
                                profile.Enabled,
                                TargetKind = profile.Target.Kind,
                                profile.Policy,
                                profile.NotifyOnRecovery,
                                profile.NotifyOnLockout
                            }).ToArray()
                        };
                    var metadata = new
                    {
                        SchemaVersion = 1,
                        CreatedUtc = DateTimeOffset.UtcNow,
                        ApplicationVersion = applicationVersion,
                        OperatingSystem = RuntimeInformation.OSDescription,
                        Architecture = RuntimeInformation.OSArchitecture.ToString(),
                        Configuration = redactedConfiguration,
                        Logging = logging is null ? null : new
                        {
                            logging.Degraded,
                            logging.QueueDepth,
                            logging.QueueDroppedCount,
                            logging.Journal.BufferedCount,
                            logging.Journal.DroppedCount
                        },
                        Events = new
                        {
                            Exported = history.Events.Count,
                            history.TotalMatches,
                            history.SkippedMalformedLines,
                            Limit = 500
                        }
                    };
                    await using (Stream entry = archive.CreateEntry("diagnostics.json",
                                     CompressionLevel.Fastest).Open())
                        await JsonSerializer.SerializeAsync(entry, metadata, Json,
                            cancellationToken).ConfigureAwait(false);
                    await using (Stream entry = archive.CreateEntry("events.jsonl",
                                     CompressionLevel.Fastest).Open())
                    await using (var writer = new StreamWriter(entry, new UTF8Encoding(false)))
                    {
                        foreach (OperationalEvent eventEntry in history.Events)
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            string line = JsonSerializer.Serialize(eventEntry with
                            {
                                ProcessIdentity = null
                            }, JsonLines);
                            await writer.WriteLineAsync(line.AsMemory(), cancellationToken)
                                .ConfigureAwait(false);
                        }
                    }
                }
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporary, output, overwrite: true);
            return new(history.Events.Count, history.SkippedMalformedLines);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    private static JsonSerializerOptions CreateJsonOptions(bool indented)
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            WriteIndented = indented
        };
        options.Converters.Add(new JsonStringEnumConverter(allowIntegerValues: false));
        return options;
    }
}

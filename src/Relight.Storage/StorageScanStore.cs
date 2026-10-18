using System.Text.Json;
using Relight.Core;

namespace Relight.Storage;

/// <summary>One latest bounded baseline. Only completed scans are saved; damaged files are preserved.</summary>
public sealed class StorageScanStore(string dataDirectory)
{
    private const int MaximumBytes = 4 * 1024 * 1024;
    private readonly string _directory = Path.Combine(Path.GetFullPath(dataDirectory), "Storage");
    private string SnapshotPath => Path.Combine(_directory, "latest-scan.json");

    public StorageScanSnapshot? Load()
    {
        if (!File.Exists(SnapshotPath)) return null;
        using var stream = new FileStream(SnapshotPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length > MaximumBytes) throw new InvalidDataException("Storage baseline exceeds its size limit.");
        var snapshot = JsonSerializer.Deserialize<StorageScanSnapshot>(stream)
            ?? throw new InvalidDataException("Storage baseline is empty.");
        Validate(snapshot);
        return snapshot;
    }

    public StorageScanSnapshot? Save(StorageScanSnapshot snapshot)
    {
        Validate(snapshot);
        Directory.CreateDirectory(_directory);
        // An exclusive cross-session lease rejects concurrent writers rather than losing a baseline.
        using var lease = new FileStream(Path.Combine(_directory, "scan.lock"), FileMode.OpenOrCreate,
            FileAccess.ReadWrite, FileShare.None);
        // Preserve invalid or unsupported prior evidence; never overwrite it silently.
        StorageScanSnapshot? previous = Load();
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(snapshot);
        if (bytes.Length > MaximumBytes) throw new InvalidDataException("Storage baseline exceeds its size limit.");
        // One owned staging path bounds leftovers if the process exits during a write.
        string temporary = Path.Combine(_directory, "latest-scan.tmp");
        try
        {
            using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write,
                       FileShare.None, 4096, FileOptions.WriteThrough))
            {
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, SnapshotPath, overwrite: true);
            return previous;
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private static void Validate(StorageScanSnapshot snapshot)
    {
        if (snapshot.SchemaVersion != 1 || string.IsNullOrWhiteSpace(snapshot.Root) ||
            !Path.IsPathFullyQualified(snapshot.Root) || snapshot.FinishedUtc < snapshot.StartedUtc ||
            snapshot.EntriesVisited is < 0 or > 100_000 || snapshot.FilesMeasured < 0 ||
            snapshot.SkippedEntries < 0 || snapshot.HardLinkAliases < 0 || snapshot.Entries is null ||
            snapshot.Entries.Length > StorageScanSnapshot.MaximumRetainedEntries || snapshot.Issues is null ||
            snapshot.Issues.Length > StorageScanSnapshot.MaximumIssues)
            throw new InvalidDataException("Storage baseline is invalid or unsupported.");
        if (snapshot.FilesMeasured > snapshot.EntriesVisited || snapshot.HardLinkAliases > snapshot.FilesMeasured ||
            snapshot.Issues.Length > snapshot.SkippedEntries)
            throw new InvalidDataException("Storage baseline counters are inconsistent.");
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string prefix = snapshot.Root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        foreach (var entry in snapshot.Entries)
        {
            if (entry is null || !Enum.IsDefined(entry.Kind) || entry.LogicalBytes < 0 || entry.AllocatedBytes < 0 ||
                string.IsNullOrWhiteSpace(entry.Path) || !paths.Add(entry.Path) ||
                (!string.Equals(entry.Path, snapshot.Root, StringComparison.OrdinalIgnoreCase) &&
                 !entry.Path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidDataException("Storage baseline entries are invalid.");
        }
        foreach (var issue in snapshot.Issues)
            if (issue is null || string.IsNullOrWhiteSpace(issue.Path) || string.IsNullOrWhiteSpace(issue.Reason))
                throw new InvalidDataException("Storage baseline coverage is invalid.");
    }
}

using System.ComponentModel;
using Relight.Core;

namespace Relight.Windows;

public sealed record StorageFileMetadata(bool IsDirectory, bool Excluded, string Identity,
    long LogicalBytes, long AllocatedBytes);

public interface IStorageScanFileSystem
{
    StorageFileMetadata Read(string path);
    IEnumerable<string> Enumerate(string directory);
}

/// <summary>One worker, metadata only; bounded traversal and retained results.</summary>
public sealed class StorageScanner(IStorageScanFileSystem? fileSystem = null)
{
    private readonly IStorageScanFileSystem _files = fileSystem ?? new WindowsStorageScanFileSystem();
    private readonly ProtectedSystemScanLocations _protectedLocations = ProtectedSystemScanLocations.CreateDefault();

    public StorageScanSnapshot Scan(string root, CancellationToken cancellationToken = default,
        IProgress<int>? progress = null, int maximumEntries = StorageScanSnapshot.DefaultEntryLimit,
        IStorageScanDiagnostics? diagnostics = null)
    {
        if (maximumEntries is < 1 or > StorageScanSnapshot.MaximumEntryLimit) throw new ArgumentOutOfRangeException(nameof(maximumEntries));
        if (string.IsNullOrWhiteSpace(root) || !Path.IsPathFullyQualified(root))
            throw new ArgumentException("Enter an absolute local folder or drive path.");
        root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        if (root.Length < 3 || root[1] != ':' || root[2] != '\\' || !char.IsAsciiLetter(root[0]))
            throw new ArgumentException("Select a folder on a local drive.");
        if (_files is WindowsStorageScanFileSystem && new DriveInfo(Path.GetPathRoot(root)!).DriveType != DriveType.Fixed)
            throw new ArgumentException("Select a folder on a fixed local drive.");
        DateTimeOffset started = DateTimeOffset.UtcNow;
        var issues = new StorageScanIssueCollector();
        var accessRestrictions = new StorageAccessRestrictionCollector(root);
        var folders = new PriorityQueue<StorageScanEntry, long>();
        var largestFiles = new PriorityQueue<StorageScanEntry, long>();
        var identities = new HashSet<string>(StringComparer.Ordinal);
        int visited = 0, measured = 0, skipped = 0, aliases = 0;
        bool limited = false;

        void Step(ScanIoOperation operation, string path, int depth) =>
            diagnostics?.Operation(operation, path, depth, visited, measured, skipped, aliases, identities.Count);
        StorageFileMetadata Read(string path, int depth, ScanIoOperation operation = ScanIoOperation.ReadMetadata)
        {
            Step(operation, path, depth);
            try { return _files.Read(path); }
            finally { diagnostics?.OperationEnded(); }
        }

        void Issue(StorageScanIssue issue, Exception? error = null)
        {
            skipped++;
            issues.Add(issue);
            accessRestrictions.Add(issue);
            diagnostics?.Issue(issue, error);
        }
        void Failure(string path, ScanIoOperation operation, Exception error) =>
            Issue(_protectedLocations.Classify(StorageScanIssue.Failure(path, operation, error)), error);

        static void Retain(PriorityQueue<StorageScanEntry, long> queue, StorageScanEntry entry, int count)
        {
            queue.Enqueue(entry, entry.AllocatedBytes);
            if (queue.Count > count) queue.Dequeue();
        }

        (long Logical, long Allocated, bool Complete) Visit(string path, int depth)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (visited >= maximumEntries) { limited = true; return (0, 0, false); }
            visited++;
            if (visited % 250 == 0) progress?.Report(visited);
            try
            {
                StorageFileMetadata metadata = Read(path, depth);
                if (metadata.Excluded)
                {
                    Issue(new(path, "Reparse point, offline or recall-on-access entry skipped.")
                        { Category = StorageScanIssueCategory.ReparseOrOffline, Operation = ScanIoOperation.ReadMetadata });
                    return (0, 0, false);
                }
                if (!metadata.IsDirectory)
                {
                    if (metadata.LogicalBytes < 0 || metadata.AllocatedBytes < 0 || string.IsNullOrEmpty(metadata.Identity))
                        throw new IOException("File size or identity unavailable.");
                    measured++;
                    // Logical totals count each path; allocated totals charge each file identity once.
                    bool firstIdentity = identities.Add(metadata.Identity);
                    long allocated = firstIdentity ? metadata.AllocatedBytes : 0;
                    if (!firstIdentity) aliases++;
                    Retain(largestFiles, new(path, StorageScanEntryKind.File,
                        metadata.LogicalBytes, allocated, true), 100);
                    return (metadata.LogicalBytes, allocated, true);
                }
                if (depth >= 128) { Issue(new(path, "Maximum directory depth reached (128).")
                    { Category = StorageScanIssueCategory.DepthLimit, Operation = ScanIoOperation.EnumerateDirectory }); return (0, 0, false); }
                long logical = 0, allocatedTotal = 0;
                bool complete = true;
                ScanIoOperation operation = ScanIoOperation.EnumerateDirectory;
                try
                {
                    Step(ScanIoOperation.EnumerateDirectory, path, depth);
                    IEnumerator<string> enumerator;
                    try { enumerator = _files.Enumerate(path).GetEnumerator(); }
                    finally { diagnostics?.OperationEnded(); }
                    using var children = enumerator;
                    while (true)
                    {
                        Step(ScanIoOperation.EnumerateDirectory, path, depth);
                        operation = ScanIoOperation.EnumerateDirectory;
                        bool next;
                        try { next = children.MoveNext(); }
                        finally { diagnostics?.OperationEnded(); }
                        if (!next) break;
                        cancellationToken.ThrowIfCancellationRequested();
                        if (visited >= maximumEntries) { limited = true; complete = false; break; }
                        var part = Visit(children.Current, depth + 1);
                        operation = ScanIoOperation.Aggregate;
                        logical = checked(logical + part.Logical);
                        allocatedTotal = checked(allocatedTotal + part.Allocated);
                        complete &= part.Complete;
                    }
                }
                catch (Exception error) when (IsReadFailure(error))
                {
                    Failure(path, operation, error);
                    complete = false;
                }
                Step(ScanIoOperation.Aggregate, path, depth);
                Retain(folders, new(path, StorageScanEntryKind.Folder, logical, allocatedTotal, complete), 2000);
                diagnostics?.OperationEnded();
                return (logical, allocatedTotal, complete);
            }
            catch (Exception error) when (IsReadFailure(error))
            {
                Failure(path, ScanIoOperation.ReadMetadata, error);
                return (0, 0, false);
            }
        }

        // An explicitly selected folder beneath a junction/cloud root is excluded too.
        bool rootAllowed = true;
        try
        {
            for (string? ancestor = root; ancestor is not null; ancestor = Path.GetDirectoryName(ancestor))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (Read(ancestor, 0, ScanIoOperation.RootValidation).Excluded)
                {
                    Issue(new(ancestor, "Selected path has a reparse/offline/recall ancestor; choose a direct local folder.")
                        { Category = StorageScanIssueCategory.ReparseOrOffline, Operation = ScanIoOperation.RootValidation });
                    rootAllowed = false;
                    break;
                }
            }
            if (rootAllowed && !Read(root, 0, ScanIoOperation.RootValidation).IsDirectory) throw new IOException("Select a folder, not a file.");
        }
        catch (Exception error) when (IsReadFailure(error)) { Failure(root, ScanIoOperation.RootValidation, error); rootAllowed = false; }
        if (rootAllowed) Visit(root, 0);
        cancellationToken.ThrowIfCancellationRequested();
        progress?.Report(visited);
        return new(1, root, started, DateTimeOffset.UtcNow, rootAllowed && !limited,
            visited, measured, skipped, aliases, limited,
            folders.UnorderedItems.Select(i => i.Element).Concat(largestFiles.UnorderedItems.Select(i => i.Element))
                .OrderByDescending(e => e.AllocatedBytes).ThenByDescending(e => e.LogicalBytes)
                .ThenBy(e => e.Path, StringComparer.OrdinalIgnoreCase).ToArray(),
            issues.Samples()) { EntryLimit = maximumEntries, IssueCounts = issues.Counts(),
                FailureClassificationVersion = StorageScanSnapshot.CurrentFailureClassificationVersion,
                AccessRestrictions = accessRestrictions.Summary() };
    }

    private static bool IsReadFailure(Exception error) => error is IOException or UnauthorizedAccessException
        or Win32Exception or OverflowException;
}

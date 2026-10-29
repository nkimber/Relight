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

    public StorageScanSnapshot Scan(string root, CancellationToken cancellationToken = default,
        IProgress<int>? progress = null, int maximumEntries = StorageScanSnapshot.DefaultEntryLimit)
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
        var issues = new List<StorageScanIssue>();
        var folders = new PriorityQueue<StorageScanEntry, long>();
        var largestFiles = new PriorityQueue<StorageScanEntry, long>();
        var identities = new HashSet<string>(StringComparer.Ordinal);
        int visited = 0, measured = 0, skipped = 0, aliases = 0;
        bool limited = false;

        void Issue(string path, string reason)
        {
            skipped++;
            if (issues.Count < StorageScanSnapshot.MaximumIssues) issues.Add(new(path, reason));
        }

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
                StorageFileMetadata metadata = _files.Read(path);
                if (metadata.Excluded)
                {
                    Issue(path, "Reparse point, offline or recall-on-access entry skipped.");
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
                if (depth >= 128) { Issue(path, "Maximum directory depth reached (128)."); return (0, 0, false); }
                long logical = 0, allocatedTotal = 0;
                bool complete = true;
                try
                {
                    foreach (string child in _files.Enumerate(path))
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        if (visited >= maximumEntries) { limited = true; complete = false; break; }
                        var part = Visit(child, depth + 1);
                        logical = checked(logical + part.Logical);
                        allocatedTotal = checked(allocatedTotal + part.Allocated);
                        complete &= part.Complete;
                    }
                }
                catch (Exception error) when (IsReadFailure(error))
                {
                    Issue(path, error.Message);
                    complete = false;
                }
                Retain(folders, new(path, StorageScanEntryKind.Folder, logical, allocatedTotal, complete), 2000);
                return (logical, allocatedTotal, complete);
            }
            catch (Exception error) when (IsReadFailure(error))
            {
                Issue(path, error.Message);
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
                if (_files.Read(ancestor).Excluded)
                {
                    Issue(ancestor, "Selected path has a reparse/offline/recall ancestor; choose a direct local folder.");
                    rootAllowed = false;
                    break;
                }
            }
            if (rootAllowed && !_files.Read(root).IsDirectory) throw new IOException("Select a folder, not a file.");
        }
        catch (Exception error) when (IsReadFailure(error)) { Issue(root, error.Message); rootAllowed = false; }
        if (rootAllowed) Visit(root, 0);
        cancellationToken.ThrowIfCancellationRequested();
        progress?.Report(visited);
        return new(1, root, started, DateTimeOffset.UtcNow, rootAllowed && !limited,
            visited, measured, skipped, aliases, limited,
            folders.UnorderedItems.Select(i => i.Element).Concat(largestFiles.UnorderedItems.Select(i => i.Element))
                .OrderByDescending(e => e.AllocatedBytes).ThenByDescending(e => e.LogicalBytes)
                .ThenBy(e => e.Path, StringComparer.OrdinalIgnoreCase).ToArray(),
            issues.ToArray()) { EntryLimit = maximumEntries };
    }

    private static bool IsReadFailure(Exception error) => error is IOException or UnauthorizedAccessException
        or Win32Exception or OverflowException;
}

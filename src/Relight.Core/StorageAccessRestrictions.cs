namespace Relight.Core;

public sealed record StorageAccessFolderCount(string Folder, int Count);

/// <summary>Local folder counts for AccessDenied only; never a complete inventory of denied paths.</summary>
public sealed record StorageAccessRestrictionSummary(int Version, StorageAccessFolderCount[] Folders, int UngroupedFailures)
{
    public const int CurrentVersion = 1;
    public const int MaximumFolders = 128;
    public const int MaximumFolderLength = 1024;
    public const int MaximumFolderDepth = 5;
}

/// <summary>Exact assigned counts with bounded folder retention and an explicit remainder.</summary>
public sealed class StorageAccessRestrictionCollector(string root)
{
    private readonly Dictionary<string, int> _folders = new(StringComparer.OrdinalIgnoreCase);
    private int _ungrouped;

    public void Add(StorageScanIssue issue)
    {
        if (issue.Category != StorageScanIssueCategory.AccessDenied) return;
        string? folder = FolderFor(root, issue);
        if (folder is null) { _ungrouped++; return; }
        if (_folders.TryGetValue(folder, out int count)) _folders[folder] = count + 1;
        else if (_folders.Count < StorageAccessRestrictionSummary.MaximumFolders) _folders.Add(folder, 1);
        else _ungrouped++;
    }

    public StorageAccessRestrictionSummary Summary() => new(StorageAccessRestrictionSummary.CurrentVersion,
        _folders.OrderByDescending(p => p.Value).ThenBy(p => p.Key, StringComparer.OrdinalIgnoreCase)
            .Select(p => new StorageAccessFolderCount(p.Key, p.Value)).ToArray(), _ungrouped);

    public static string? FolderFor(string root, StorageScanIssue issue)
    {
        try
        {
            root = Normalize(root);
            string path = Normalize(issue.Path);
            string prefix = root.TrimEnd('\\') + '\\';
            if (!string.Equals(path, root, StringComparison.OrdinalIgnoreCase) &&
                !path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return null;
            // A failed metadata open cannot establish whether the entry is a file or folder.
            // Count it under its parent; listing/root-validation failures belong to that folder.
            string folder = issue.Operation == ScanIoOperation.ReadMetadata &&
                !string.Equals(path, root, StringComparison.OrdinalIgnoreCase)
                ? Path.GetDirectoryName(path)! : path;
            string relative = Path.GetRelativePath(root, folder);
            string[] parts = relative == "." ? [] : relative.Split('\\');
            folder = parts.Length == 0 ? root : Path.Combine([root, .. parts.Take(StorageAccessRestrictionSummary.MaximumFolderDepth)]);
            return folder.Length <= StorageAccessRestrictionSummary.MaximumFolderLength ? folder : null;
        }
        catch (Exception error) when (error is ArgumentException or NotSupportedException or IOException) { return null; }
    }

    private static string Normalize(string path)
    {
        if (!Path.IsPathFullyQualified(path)) throw new ArgumentException("Use an absolute local path.");
        path = Path.GetFullPath(path);
        if (path.Length < 3 || !char.IsAsciiLetter(path[0]) || path[1] != ':' || path[2] != '\\')
            throw new ArgumentException("Use a local drive path.");
        return Path.TrimEndingDirectorySeparator(path);
    }
}

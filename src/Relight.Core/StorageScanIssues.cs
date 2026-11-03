using System.ComponentModel;

namespace Relight.Core;

public enum StorageScanIssueKind { Unknown, Excluded, Failed, Limited }
public enum StorageScanIssueCategory
{
    Unknown, ReparseOrOffline, DepthLimit, AccessDenied, MissingOrChanged, SharingViolation,
    MetadataUnavailable, EnumerationFailure, NativeIoFailure, SizeOverflow, ProtectedSystemAccess
}

public sealed record StorageScanIssue(string Path, string Reason)
{
    public StorageScanIssueCategory Category { get; init; }
    public ScanIoOperation? Operation { get; init; }
    public int? NativeErrorCode { get; init; }
    public int? HResult { get; init; }
    public StorageScanIssueKind Kind => KindOf(Category);

    public static StorageScanIssueKind KindOf(StorageScanIssueCategory category) => category switch
    {
        StorageScanIssueCategory.Unknown => StorageScanIssueKind.Unknown,
        StorageScanIssueCategory.ReparseOrOffline => StorageScanIssueKind.Excluded,
        StorageScanIssueCategory.DepthLimit => StorageScanIssueKind.Limited,
        _ => StorageScanIssueKind.Failed
    };

    public static StorageScanIssue Failure(string path, ScanIoOperation operation, Exception error)
    {
        int? native = error is Win32Exception windows ? windows.NativeErrorCode : null;
        if (native is null && ((uint)error.HResult & 0xFFFF0000) == 0x80070000)
            native = error.HResult & 0xFFFF;
        var category = error switch
        {
            UnauthorizedAccessException => StorageScanIssueCategory.AccessDenied,
            FileNotFoundException or DirectoryNotFoundException => StorageScanIssueCategory.MissingOrChanged,
            OverflowException => StorageScanIssueCategory.SizeOverflow,
            _ when native == 5 => StorageScanIssueCategory.AccessDenied,
            _ when native is 2 or 3 => StorageScanIssueCategory.MissingOrChanged,
            _ when native is 32 or 33 => StorageScanIssueCategory.SharingViolation,
            _ when native is not null => StorageScanIssueCategory.NativeIoFailure,
            _ when operation == ScanIoOperation.EnumerateDirectory => StorageScanIssueCategory.EnumerationFailure,
            _ => StorageScanIssueCategory.MetadataUnavailable
        };
        return new(path, error.Message) { Category = category, Operation = operation,
            NativeErrorCode = native, HResult = error.HResult };
    }
}

public sealed record StorageScanIssueCount(StorageScanIssueCategory Category, int Count);

/// <summary>Exact finite-category counts and first/recent samples; no growing path inventory.</summary>
public sealed class StorageScanIssueCollector
{
    public const int SamplesPerCategory = 10;
    private readonly Dictionary<StorageScanIssueCategory, int> _counts = new();
    private readonly Dictionary<StorageScanIssueCategory, List<StorageScanIssue>> _samples = new();
    public int Total { get; private set; }

    public void Add(StorageScanIssue issue)
    {
        if (!Enum.IsDefined(issue.Category) || issue.Category == StorageScanIssueCategory.Unknown)
            throw new ArgumentException("A new scan issue must have a known category.", nameof(issue));
        Total++;
        _counts.TryGetValue(issue.Category, out int count); _counts[issue.Category] = count + 1;
        if (!_samples.TryGetValue(issue.Category, out var samples)) _samples[issue.Category] = samples = [];
        if (samples.Count == SamplesPerCategory) samples.RemoveAt(SamplesPerCategory / 2);
        samples.Add(issue);
    }

    public StorageScanIssueCount[] Counts() => _counts.OrderBy(p => p.Key).Select(p => new StorageScanIssueCount(p.Key, p.Value)).ToArray();
    public StorageScanIssue[] Samples() => _samples.OrderBy(p => p.Key).SelectMany(p => p.Value).ToArray();
}

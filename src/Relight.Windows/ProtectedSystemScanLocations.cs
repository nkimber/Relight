using Relight.Core;

namespace Relight.Windows;

/// <summary>Location labels only: no additional exclusions, permission changes or filesystem reads.</summary>
internal sealed class ProtectedSystemScanLocations
{
    private readonly string[] _trees;
    private readonly string[] _items;

    internal ProtectedSystemScanLocations(string windowsDirectory, string commonDataDirectory)
    {
        string windows = Normalize(windowsDirectory), data = Normalize(commonDataDirectory);
        string drive = Path.GetPathRoot(windows)!;
        _trees = [Path.Combine(data, @"Microsoft\Crypto\Keys"), Path.Combine(data, @"Microsoft\Crypto\SystemKeys"),
            Path.Combine(data, @"Microsoft\Crypto\RSA\MachineKeys"), Path.Combine(data, @"Microsoft\Crypto\DSS\MachineKeys"),
            Path.Combine(windows, @"System32\config"), Path.Combine(drive, "$Recycle.Bin"), Path.Combine(drive, "System Volume Information")];
        _items = [Path.Combine(windows, "Temp"), .. new[] { "DumpStack.log", "DumpStack.log.tmp", "hiberfil.sys", "pagefile.sys", "swapfile.sys" }
            .Select(name => Path.Combine(drive, name))];
    }

    internal static ProtectedSystemScanLocations CreateDefault() => new(
        Environment.GetFolderPath(Environment.SpecialFolder.Windows),
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData));

    internal StorageScanIssue Classify(StorageScanIssue issue)
    {
        if (issue.Category is not (StorageScanIssueCategory.AccessDenied or StorageScanIssueCategory.SharingViolation) ||
            issue.NativeErrorCode is not (5 or 32 or 33)) return issue;
        string path;
        try { path = Normalize(issue.Path); }
        catch (Exception error) when (error is ArgumentException or NotSupportedException or IOException) { return issue; }
        bool known = _items.Contains(path, StringComparer.OrdinalIgnoreCase) || _trees.Any(root =>
            string.Equals(root, path, StringComparison.OrdinalIgnoreCase) || path.StartsWith(root + '\\', StringComparison.OrdinalIgnoreCase));
        return known ? issue with { Category = StorageScanIssueCategory.ProtectedSystemAccess } : issue;
    }

    private static string Normalize(string path)
    {
        if (!Path.IsPathFullyQualified(path)) throw new ArgumentException("Use an absolute local path.", nameof(path));
        path = Path.GetFullPath(path);
        if (path.Length < 3 || !char.IsAsciiLetter(path[0]) || path[1] != ':' || path[2] != '\\')
            throw new ArgumentException("Use a local drive path.", nameof(path));
        return Path.TrimEndingDirectorySeparator(path);
    }
}

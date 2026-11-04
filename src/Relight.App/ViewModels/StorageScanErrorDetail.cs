using Relight.Core;

namespace Relight.ViewModels;

internal sealed record StorageScanErrorDetail(string Path, string Reason, string Operation, string WindowsCode,
    string HResult, string Error, string Explanation, string NextSteps)
{
    public string Report => $"Location: {Path}\nError: {Error}\nOperation: {Operation}\nWindows code: {WindowsCode}\nHRESULT: {HResult}\n" +
        $"Windows / scanner message: {Reason}\n\nWhat happened: {Explanation}\n\nSuggested next steps: {NextSteps}";

    public static StorageScanErrorDetail For(StorageScanIssue issue)
    {
        var (error, explanation, nextSteps) = Advice(issue.Category, issue.NativeErrorCode);
        if (issue.Operation == ScanIoOperation.RootValidation && issue.Category == StorageScanIssueCategory.MetadataUnavailable)
            nextSteps = "Select an existing direct folder on a fixed local drive, then rescan. If it remains unavailable, copy these details and retain the diagnostic logs for investigation.";
        return new(issue.Path, issue.Reason, issue.Operation switch
        {
            ScanIoOperation.RootValidation => "Validate scan root",
            ScanIoOperation.ReadMetadata => "Read metadata",
            ScanIoOperation.EnumerateDirectory => "List folder contents",
            ScanIoOperation.Aggregate => "Calculate folder totals",
            _ => "Not recorded"
        }, issue.NativeErrorCode?.ToString() ?? "Not recorded",
            issue.HResult is { } hresult ? $"0x{unchecked((uint)hresult):X8}" : "Not recorded", error, explanation, nextSteps);
    }

    internal static (string Error, string Explanation, string NextSteps) Advice(StorageScanIssueCategory category, int? code = null) => category switch
    {
        StorageScanIssueCategory.MissingOrChanged => ("Missing or changed path",
            "The path could not be found during this live scan. It may have moved, disappeared or changed while an application was updating files.",
            "Let file moves or application updates finish, then rescan the containing folder. If it repeats, check whether the exact path still exists and retain these details."),
        StorageScanIssueCategory.SharingViolation => ("File or folder in use",
            "Windows reported an incompatible open handle or file lock held by another operation.",
            "Let the owning operation finish, or close its application normally when convenient, then rescan this folder. Keep valuable running work active until it finishes."),
        StorageScanIssueCategory.NativeIoFailure when code is 23 or 1117 => ("Windows device or data-read error",
            "Windows reported a device I/O error or data checksum error. This alone does not identify a failed disk.",
            "Rescan the containing folder once. If the error repeats, retain the diagnostic logs and ask your administrator to check storage health and Windows system events. Preserve important data before any repair."),
        StorageScanIssueCategory.NativeIoFailure when code is 1 or 50 or 87 => ("Metadata request rejected by Windows",
            "Windows reported an unsupported operation or invalid request parameter for this metadata read.",
            "Rescan the folder with the current Relight build. If it repeats, copy these details and retain diagnostic logs for the Relight maintainer to investigate filesystem or adapter compatibility."),
        StorageScanIssueCategory.NativeIoFailure when code == 206 => ("Windows path length error",
            "Windows rejected the path length during the metadata operation.",
            "Rescan a nearby parent folder with the current Relight build. If it repeats, send these details to the Relight maintainer to investigate long-path handling."),
        StorageScanIssueCategory.NativeIoFailure => ("Windows I/O error",
            "A native Windows metadata operation failed. The recorded code and message describe the reported error; the underlying cause needs investigation.",
            "Rescan the containing folder once. If it repeats, copy these details and retain the scan-failures logs using Open diagnostic logs for investigation."),
        StorageScanIssueCategory.EnumerationFailure => ("Folder listing interrupted",
            "Relight could not finish listing this folder. Children that were not returned were not measured.",
            "Let changes in this folder finish, then scan it directly. If the listing still fails, copy these details and retain diagnostic logs for investigation."),
        StorageScanIssueCategory.SizeOverflow => ("Folder size total overflow",
            "A size total exceeded the supported integer range, so that total could not be calculated reliably.",
            "Scan smaller subfolders separately and share these details and diagnostic logs with the Relight maintainer."),
        _ => ("Metadata could not be verified",
            "Relight could not verify the entry's identity, size or canonical location. Files changing or resolving through aliases can cause this safeguard to reject a measurement.",
            "Choose a direct local folder and wait for file changes to finish, then rescan. If it repeats, copy these details and retain diagnostic logs; the scanner safeguard requires investigation.")
    };
}

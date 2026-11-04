using System.Collections.Generic;
using Relight.Core;

namespace Relight.ViewModels;

/// <summary>Warning prominence is independent of whether every location was measured.</summary>
internal sealed record StorageScanPresentation(bool Warn, string WarningTitle, string Explanation, string Status, string CoverageNote)
{
    public static StorageScanPresentation For(StorageScanSnapshot scan)
    {
        bool unknown = !scan.CoverageComplete && scan.OtherErrorEntries is null;
        bool depth = scan.LimitedEntries > 0;
        bool errors = scan.OtherErrorEntries > 0;
        var reasons = new List<string>();
        if (scan.EntryLimitReached) reasons.Add($"Stopped after {scan.EntriesVisited:N0} entries (limit {scan.EntryLimit:N0}) in {scan.Root}. Increase the scan entry limit or select a smaller folder, then start a new scan.");
        if (!scan.TraversalComplete && !scan.EntryLimitReached) reasons.Add("Traversal did not finish. Check that the selected folder is available and review the coverage details before rescanning.");
        if (depth) reasons.Add($"Directory depth was limited {scan.LimitedEntries:N0} times. Scan a subfolder closer to the deepest files to measure those locations.");
        if (errors) reasons.Add($"{scan.OtherErrorEntries:N0} other read errors occurred. Review Other read errors below for affected paths, Windows codes and suggested next steps.");
        if (unknown) reasons.Add("This older result does not establish the other-error breakdown. Rescan to classify its coverage gaps.");
        if (reasons.Count > 0)
        {
            string title = scan.EntryLimitReached ? "SCAN STOPPED — ENTRY LIMIT REACHED" :
                !scan.TraversalComplete ? "SCAN UNFINISHED — COVERAGE INCOMPLETE" :
                depth ? "SCAN LIMITED — DIRECTORY DEPTH REACHED" : errors ? "SCAN COMPLETED WITH ERRORS" : "SCAN COVERAGE UNKNOWN";
            string status = scan.EntryLimitReached ? "Scan stopped at the entry limit." :
                !scan.TraversalComplete ? "Scan traversal did not finish." :
                depth ? "Scan completed with directory depth limits." : errors ? "Scan completed with errors." : "Scan coverage is unknown.";
            return new(true, title, string.Join(" ", reasons) + " Rankings are incomplete. Larger files may be missing.", status, "");
        }
        string completed = scan.CoverageComplete ? "Scan completed." :
            scan.ExcludedEntries > 0 && scan.FailedEntries > 0 ? "Scan completed with exclusions and access restrictions." :
            scan.ExcludedEntries > 0 ? "Scan completed with exclusions." : "Scan completed with access restrictions.";
        return new(false, "", "", completed, scan.CoverageComplete ? "" :
            "Some locations were excluded or inaccessible and were not measured. Rankings cover measured locations only.");
    }
}

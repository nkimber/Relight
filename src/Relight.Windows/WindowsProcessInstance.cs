using System.Globalization;
using Relight.Storage;

namespace Relight.Windows;

/// <summary>
/// Presentation metadata from the two supported Windows adapters' verified
/// identity formats. This never opens a process or authorizes a process action.
/// </summary>
public sealed record WindowsProcessInstance(int ProcessId, DateTimeOffset StartedUtc)
{
    public static WindowsProcessInstance? FromIdentity(string? identity, TargetKind kind)
    {
        string[] parts = identity?.Split('|') ?? [];
        if (parts.Length != 4 ||
            !int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out _) ||
            !int.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out int pid) ||
            pid <= 0 ||
            !long.TryParse(parts[3], NumberStyles.None, CultureInfo.InvariantCulture, out long ticks) ||
            ticks <= 0 || ticks > DateTime.MaxValue.Ticks)
            return null;
        bool supported = kind switch
        {
            TargetKind.PackagedApplication => parts[1] == ChatGptPackagedDiscovery.PackageFamilyName,
            TargetKind.Executable => Path.IsPathFullyQualified(parts[1]) &&
                string.Equals(Path.GetExtension(parts[1]), ".exe", StringComparison.OrdinalIgnoreCase),
            _ => false
        };
        return supported ? new(pid, new DateTimeOffset(ticks, TimeSpan.Zero)) : null;
    }
}

using System.ComponentModel;
using System.Runtime.InteropServices;
using Relight.Core;

namespace Relight.Windows;

public sealed record DiskSpaceReading(string Drive, DiskSpaceLevel Level, long? AvailableBytes,
    long? TotalBytes, DateTimeOffset CheckedUtc, string? Problem, bool Notify)
{
    public string FreeSpace => AvailableBytes is { } free && TotalBytes is > 0
        ? $"{free / 1_073_741_824d:N1} GiB ({100d * free / TotalBytes.Value:N1}%) free"
        : "Unknown";
    public string Status => $"{Drive} · {Level} · {FreeSpace}" +
        (Problem is null ? "" : $" · {Problem}");
}

public sealed class DiskSpaceMonitor
{
    private readonly Dictionary<string, (DiskSpaceDrivePolicy Policy, DiskSpaceAlert Alert)> _alerts =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Func<string, (long Available, long Total)> _read;

    public DiskSpaceMonitor(Func<string, (long Available, long Total)>? read = null) =>
        _read = read ?? ReadWindows;

    public static string SystemDrive => Path.GetPathRoot(Environment.SystemDirectory)!;
    public static DiskSpaceDrivePolicy[] EffectiveDrives(DiskSpaceSettings settings) =>
        settings.Drives.Length == 0 ? [new(SystemDrive)] : settings.Drives;

    // Called serially on a worker, independently of recovery scheduling.
    public IReadOnlyList<DiskSpaceReading> Sample(DiskSpaceSettings settings)
    {
        settings.Validate();
        if (!settings.Enabled) { _alerts.Clear(); return []; }
        var drives = EffectiveDrives(settings);
        foreach (string old in _alerts.Keys.Except(drives.Select(d => d.Root),
                     StringComparer.OrdinalIgnoreCase).ToArray()) _alerts.Remove(old);
        return drives.Select(policy =>
        {
            if (!_alerts.TryGetValue(policy.Root, out var state) || state.Policy != policy)
                _alerts[policy.Root] = state = (policy, new DiskSpaceAlert());
            DateTimeOffset now = DateTimeOffset.UtcNow;
            try
            {
                var (available, total) = _read(policy.Root);
                var level = state.Alert.Evaluate(policy, available, total);
                bool notify = state.Alert.Observe(level) && settings.NotificationsEnabled;
                return new DiskSpaceReading(policy.Root, level, available, total, now,
                    level == DiskSpaceLevel.Unavailable ? "Invalid capacity reading" : null, notify);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or Win32Exception)
            {
                return new DiskSpaceReading(policy.Root, DiskSpaceLevel.Unavailable,
                    null, null, now, error.Message, false);
            }
        }).ToArray();
    }

    private static (long, long) ReadWindows(string root)
    {
        // Exclude network-mapped and removable drives from this first local-drive slice.
        if (new DriveInfo(root).DriveType != DriveType.Fixed)
            throw new IOException("Drive is unavailable or is not a fixed local drive.");
        if (!GetDiskFreeSpaceEx(root, out long available, out long total, out _))
            throw new Win32Exception(Marshal.GetLastWin32Error());
        return (available, total);
    }

    [DllImport("kernel32.dll", EntryPoint = "GetDiskFreeSpaceExW", CharSet = CharSet.Unicode,
        SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetDiskFreeSpaceEx(string directory, out long available,
        out long total, out long totalFree);
}

using System.ComponentModel;
using System.Runtime.InteropServices;
using Relight.Core;

namespace Relight.Windows;

public sealed record DiskSpaceReading(string Drive, DiskSpaceLevel Level, long? AvailableBytes,
    long? TotalBytes, DateTimeOffset CheckedUtc, string? Problem, bool Notify)
{
    public bool IsReminder { get; init; }
    public string FreeSpace => AvailableBytes is { } free && TotalBytes is > 0
        ? $"{free / 1_073_741_824d:N1} GiB ({100d * free / TotalBytes.Value:N1}%) free"
        : "Unknown";
    public string Status => $"{Drive} · {Level} · {FreeSpace}" +
        (Problem is null ? "" : $" · {Problem}");
}

public sealed class DiskSpaceMonitor
{
    private readonly Dictionary<string, (DiskSpaceDrivePolicy Policy, DiskSpaceAlert Alert, DiskSpaceNotificationSchedule Notices)> _alerts =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Func<string, (long Available, long Total)> _read;
    private readonly TimeProvider _clock;
    private readonly object _snoozeSync = new();
    private long _snoozeStarted, _snoozeVersion;
    private TimeSpan _snoozeDuration;

    public DiskSpaceMonitor(Func<string, (long Available, long Total)>? read = null, TimeProvider? clock = null)
    { _read = read ?? ReadWindows; _clock = clock ?? TimeProvider.System; }

    public TimeSpan SnoozeRemaining
    {
        get
        {
            lock (_snoozeSync)
            {
                var remaining = _snoozeDuration - _clock.GetElapsedTime(_snoozeStarted);
                return remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero;
            }
        }
    }

    public void SnoozeNotifications(TimeSpan duration)
    {
        if (duration < TimeSpan.FromMinutes(5) || duration > TimeSpan.FromDays(1))
            throw new ArgumentException("Snooze must be 5–1440 minutes.");
        lock (_snoozeSync) { _snoozeStarted = _clock.GetTimestamp(); _snoozeDuration = duration; _snoozeVersion++; }
    }

    public void ResumeNotifications()
    {
        lock (_snoozeSync) _snoozeDuration = TimeSpan.Zero;
    }

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
                _alerts[policy.Root] = state = (policy, new DiskSpaceAlert(), new DiskSpaceNotificationSchedule(_clock));
            DateTimeOffset now = _clock.GetUtcNow();
            try
            {
                var (available, total) = _read(policy.Root);
                var level = state.Alert.Evaluate(policy, available, total);
                state.Alert.Observe(level);
                (bool notify, bool reminder) = Notice(level);
                return new DiskSpaceReading(policy.Root, level, available, total, now,
                    level == DiskSpaceLevel.Unavailable ? "Invalid capacity reading" : null, notify) { IsReminder = reminder };
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or Win32Exception)
            {
                Notice(DiskSpaceLevel.Unavailable);
                return new DiskSpaceReading(policy.Root, DiskSpaceLevel.Unavailable,
                    null, null, now, error.Message, false);
            }
            (bool, bool) Notice(DiskSpaceLevel level)
            {
                lock (_snoozeSync)
                    return state.Notices.Observe(level, settings.NotificationsEnabled, settings.ReminderIntervalMinutes,
                        SnoozeRemaining > TimeSpan.Zero, _snoozeVersion);
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

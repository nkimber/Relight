namespace Relight.Core;

public enum DiskSpaceLevel { Healthy, Warning, Critical, Unavailable }

public sealed record DiskSpaceDrivePolicy(string Root, double WarningPercent = 10,
    double CriticalPercent = 5, double WarningGigabytes = 20, double CriticalGigabytes = 5)
{
    public void Validate()
    {
        if (Root is null || Root.Length != 3 || !char.IsAsciiLetter(Root[0]) ||
            Root[1] != ':' || Root[2] != '\\')
            throw new ArgumentException("Select a local drive root such as C:\\.");
        if (!double.IsFinite(WarningPercent) || !double.IsFinite(CriticalPercent) ||
            !double.IsFinite(WarningGigabytes) || !double.IsFinite(CriticalGigabytes) ||
            CriticalPercent <= 0 || WarningPercent <= CriticalPercent || WarningPercent > 95 ||
            CriticalGigabytes <= 0 || WarningGigabytes <= CriticalGigabytes || WarningGigabytes > 1_000_000)
            throw new ArgumentException("Warning thresholds must exceed positive critical thresholds; warning percent must be at most 95.");
    }
}

public sealed record DiskSpaceSettings
{
    public bool Enabled { get; init; } = true;
    public bool NotificationsEnabled { get; init; } = true;
    public int CheckIntervalSeconds { get; init; } = 60;
    // Zero retains once-per-severity alerts without repeated reminders.
    public int ReminderIntervalMinutes { get; init; }
    // Empty means the Windows system drive. Explicit entries replace that default.
    public DiskSpaceDrivePolicy[] Drives { get; init; } = [];

    public void Validate()
    {
        if (CheckIntervalSeconds is < 10 or > 3600 || Drives is null || Drives.Length > 26)
            throw new ArgumentException("Storage interval must be 10–3600 seconds, with at most 26 drives.");
        if (ReminderIntervalMinutes != 0 && (ReminderIntervalMinutes is < 15 or > 10080))
            throw new ArgumentException("Reminder interval must be 0 (off), or 15–10080 minutes.");
        var roots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var drive in Drives)
        {
            if (drive is null) throw new ArgumentException("Storage drive is required.");
            drive.Validate();
            if (!roots.Add(drive.Root)) throw new ArgumentException("Storage drives must be unique.");
        }
    }
}

/// <summary>Per-drive notification cadence, independent of detection and deletion authority.</summary>
public sealed class DiskSpaceNotificationSchedule(TimeProvider clock)
{
    private DiskSpaceLevel _highestNotified;
    private long? _lastNotice;
    private long _snoozeVersion;

    public (bool Notify, bool Reminder) Observe(DiskSpaceLevel level, bool enabled, int reminderMinutes,
        bool snoozed, long snoozeVersion)
    {
        long now = clock.GetTimestamp();
        if (_snoozeVersion != snoozeVersion)
        {
            _snoozeVersion = snoozeVersion;
            _highestNotified = DiskSpaceLevel.Healthy;
            _lastNotice = null; // Fresh low-space evaluation after this explicit snooze, even with reminders off.
        }
        if (level == DiskSpaceLevel.Healthy)
        {
            _highestNotified = DiskSpaceLevel.Healthy; _lastNotice = null;
            return (false, false);
        }
        if (level == DiskSpaceLevel.Unavailable || !enabled || snoozed)
        {
            if (_lastNotice is not null) _lastNotice = now;
            return (false, false);
        }
        bool reminder = level <= _highestNotified;
        if (reminder && (reminderMinutes == 0 || _lastNotice is not { } last ||
            clock.GetElapsedTime(last, now) < TimeSpan.FromMinutes(reminderMinutes))) return (false, false);
        if (level > _highestNotified) _highestNotified = level;
        _lastNotice = now;
        return (true, reminder);
    }
}

/// <summary>Transition policy; unknown readings preserve the prior alert episode.</summary>
public sealed class DiskSpaceAlert
{
    public DiskSpaceLevel Level { get; private set; } = DiskSpaceLevel.Healthy;
    private DiskSpaceLevel _highestNotified;

    public DiskSpaceLevel Evaluate(DiskSpaceDrivePolicy policy, long available, long total)
    {
        if (total <= 0 || available < 0 || available > total) return DiskSpaceLevel.Unavailable;
        double percent = 100d * available / total;
        double gb = available / 1_073_741_824d;
        DiskSpaceLevel raw = percent < policy.CriticalPercent || gb < policy.CriticalGigabytes
            ? DiskSpaceLevel.Critical
            : percent < policy.WarningPercent || gb < policy.WarningGigabytes
                ? DiskSpaceLevel.Warning : DiskSpaceLevel.Healthy;
        if (Level == DiskSpaceLevel.Critical && raw != DiskSpaceLevel.Critical &&
            (percent < Math.Min(policy.CriticalPercent + 2, policy.WarningPercent + 2) ||
             gb < Math.Min(policy.CriticalGigabytes + 1, policy.WarningGigabytes + 1)))
            return DiskSpaceLevel.Critical;
        // Clear an episode only above both warning thresholds plus a margin.
        if (raw == DiskSpaceLevel.Healthy && Level != DiskSpaceLevel.Healthy &&
            (percent < policy.WarningPercent + 2 || gb < policy.WarningGigabytes + 1))
            return Level;
        return raw;
    }

    public bool Observe(DiskSpaceLevel level)
    {
        if (level == DiskSpaceLevel.Unavailable) return false;
        bool notify = (level is DiskSpaceLevel.Warning or DiskSpaceLevel.Critical) && level > _highestNotified;
        if (level == DiskSpaceLevel.Healthy) _highestNotified = DiskSpaceLevel.Healthy;
        else if (level > _highestNotified) _highestNotified = level;
        Level = level;
        return notify;
    }
}

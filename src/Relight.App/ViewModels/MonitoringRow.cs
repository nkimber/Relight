using System;
using System.ComponentModel;
using Relight.Core;
using Relight.Storage;
using Relight.Windows;

namespace Relight.ViewModels;

internal sealed record MonitoringPresentation(string Name, string Status, string ProcessId,
    string Uptime, string LastVerified, string AutomaticRelaunches, string Attempts, string Detail)
{
    public static MonitoringPresentation FromProfile(HostedProfileStatus profile,
        ApplicationStatusRow application, TimeSpan elapsed, DateTimeOffset nowUtc,
        DashboardEventHistory? history, string? historyProblem, bool loggingDegraded)
    {
        RecoverySnapshot? recovery = profile.Recovery;
        // A stale or unavailable detection cannot assert that a process is still
        // running. Age is frozen at its last verified sighting, not extrapolated.
        WindowsProcessInstance? instance = profile.Monitoring && profile.ConfiguredEnabled &&
            profile.Problem is null && recovery is { Enabled: true, Paused: false,
            DetectionUnavailable: false, TargetIdentity: not null, LastVerifiedAt: not null }
            ? WindowsProcessInstance.FromIdentity(recovery.TargetIdentity, profile.TargetKind)
            : null;
        string uptime = "—";
        if (instance is not null && recovery!.LastVerifiedAt is { } verified && elapsed >= verified)
        {
            TimeSpan age = nowUtc - (elapsed - verified) - instance.StartedUtc;
            if (age >= TimeSpan.Zero) uptime = Duration(age);
        }
        string launches = historyProblem is not null ? "Unavailable"
            : history is null ? "Loading…"
            : (history.Profiles.TryGetValue(profile.Id, out var milestone)
                ? milestone.AutomaticLaunches24Hours : 0).ToString() +
                (history.SkippedMalformedLines > 0 || loggingDegraded ? " (partial)" : "");
        string lastVerified = recovery?.LastVerifiedAt is { } seen && elapsed >= seen
            ? $"{Duration(elapsed - seen)} ago" : "—";
        string attempts = recovery is null ? "—" : profile.Policy is null
            ? recovery.ReservedAutomaticAttempts.ToString()
            : $"{recovery.ReservedAutomaticAttempts} / {profile.Policy.MaximumAutomaticAttempts}";
        return new(profile.Name, application.State, instance?.ProcessId.ToString() ?? "—",
            uptime, lastVerified, launches, attempts, application.NextAction);
    }

    private static string Duration(TimeSpan span)
    {
        if (span.TotalDays >= 1) return $"{(int)span.TotalDays}d {span.Hours}h {span.Minutes}m";
        if (span.TotalHours >= 1) return $"{(int)span.TotalHours}h {span.Minutes}m";
        if (span.TotalMinutes >= 1) return $"{(int)span.TotalMinutes}m {span.Seconds}s";
        return $"{(int)Math.Max(0, span.TotalSeconds)}s";
    }
}

internal sealed class MonitoringRow(Guid id, MonitoringPresentation presentation) : INotifyPropertyChanged
{
    public Guid Id { get; } = id;
    public MonitoringPresentation Presentation { get; private set; } = presentation;
    public string Name => Presentation.Name;
    public string Status => Presentation.Status;
    public string ProcessId => Presentation.ProcessId;
    public string Uptime => Presentation.Uptime;
    public string LastVerified => Presentation.LastVerified;
    public string AutomaticRelaunches => Presentation.AutomaticRelaunches;
    public string Attempts => Presentation.Attempts;
    public string Detail => Presentation.Detail;

    public void Update(MonitoringPresentation next)
    {
        if (Presentation == next) return;
        Presentation = next;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(null));
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}

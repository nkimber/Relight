using System;
using Relight.Core;
using Relight.Windows;

namespace Relight.ViewModels;

public sealed record ProfileTimingPresentation(
    string NextAction, string LastSeen, string Attempts, string Instance)
{
    public static ProfileTimingPresentation FromProfile(HostedProfileStatus profile,
        TimeSpan elapsed)
    {
        ArgumentNullException.ThrowIfNull(profile);
        RecoverySnapshot? recovery = profile.Recovery;
        RecoveryPolicy? policy = profile.Policy;
        string attempts = recovery is null ? "Attempt budget unavailable"
            : policy is null ? $"{recovery.ReservedAutomaticAttempts} automatic attempt(s) reserved"
            : $"Automatic attempts: {recovery.ReservedAutomaticAttempts} / " +
              $"{policy.MaximumAutomaticAttempts} this episode";
        string lastSeen = recovery?.LastVerifiedAt is { } verified
            ? $"Last verified this session: {Duration(elapsed - verified)} ago"
            : "No verified sighting this session";
        string instance = recovery?.TargetIdentity is null
            ? "No current-session instance verified"
            : "Matching current-session instance verified";
        string next = DescribeNextAction(profile, elapsed);
        return new(next, lastSeen, attempts, instance);
    }

    private static string DescribeNextAction(HostedProfileStatus profile, TimeSpan elapsed)
    {
        RecoverySnapshot? recovery = profile.Recovery;
        RecoveryPolicy? policy = profile.Policy;
        if (profile.Problem is not null || recovery?.DetectionUnavailable == true)
            return "Detection or recovery is unavailable; automatic actions are suspended.";
        if (!profile.ConfiguredEnabled || recovery?.Enabled == false)
            return "Protection disabled; the application is left running.";
        if (recovery?.Paused == true)
            return "Protection paused; no automatic action is scheduled.";
        if (recovery?.HoldReason is not null || recovery?.LockedOut == true)
            return "Automatic launches suspended; still checking for the application.";
        if (recovery?.State == RecoveryState.Starting &&
            recovery.AppearanceDeadline is { } appearance)
            return elapsed < appearance
                ? $"Appearance timeout in {Duration(appearance - elapsed)}; checking for the app."
                : "Appearance timeout reached; awaiting a confirming check.";
        if (recovery?.State == RecoveryState.RetryWaiting &&
            recovery.RetryDeadline is { } retry)
            return elapsed < retry
                ? $"Retry eligible in {Duration(retry - elapsed)}; identity is rechecked first."
                : "Retry eligible; verifying target identity before any launch.";
        if (recovery?.State == RecoveryState.Observing && policy is not null &&
            recovery.ObservationStartedAt is { } started)
        {
            if (recovery.LastVerifiedAt is { } last &&
                elapsed - last > policy.ObservationPollInterval + TimeSpan.FromSeconds(5))
                return "Observation check overdue; continuity is unverified.";
            TimeSpan remaining = policy.ObservationPeriod - (elapsed - started);
            return remaining > TimeSpan.Zero
                ? $"Stability qualification in {Duration(remaining)} if observation stays continuous."
                : "Stability time reached; awaiting a confirming check.";
        }
        return recovery?.State switch
        {
            RecoveryState.WaitingForFirstStart => "Waiting for the initial application start.",
            RecoveryState.Healthy => "Monitoring a verified application instance.",
            RecoveryState.AwaitingIntervention => "Automatic launches suspended; still checking for the app.",
            _ => "Waiting for the next monitoring check."
        };
    }

    private static string Duration(TimeSpan interval)
    {
        int seconds = (int)Math.Ceiling(Math.Max(0, interval.TotalSeconds));
        if (seconds < 60) return $"{seconds}s";
        int minutes = seconds / 60;
        int remainder = seconds % 60;
        return minutes < 60 ? $"{minutes}m {remainder}s"
            : $"{minutes / 60}h {minutes % 60}m";
    }
}

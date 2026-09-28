using System;
using Relight.Core;

namespace Relight.ViewModels;

public static class RecoveryPolicyPreview
{
    public static string Describe(RecoveryPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(policy);
        policy.Validate();
        string attempts = policy.MaximumAutomaticAttempts == 0
            ? "Automatic starts are off; detection and explicit Start now remain available."
            : $"Confirm absence for {Duration(policy.AbsenceConfirmationDelay)}, then wait " +
              $"{Duration(policy.RetryDelay)} before each automatic start. Try up to " +
              $"{policy.MaximumAutomaticAttempts} per episode; when exhausted, keep checking " +
              $"every {Duration(policy.LockoutDiscoveryInterval)} while waiting for intervention.";
        string initial = policy.MaximumAutomaticAttempts == 0
            ? "If the app is initially absent, Relight waits for an explicit start."
            : policy.StartAutomaticallyWhenInitiallyAbsent
                ? "If the app is initially absent, Relight may start it automatically after the configured checks and delay."
                : "If the app is initially absent, Relight waits for your first launch.";
        string rearm = policy.RearmAfterStableExternalStart
            ? "A stable external start can rearm protection after lockout."
            : "An external start does not clear lockout; use Reset recovery.";
        return $"Check every {Duration(policy.NormalPollInterval)}. After a start, " +
               $"check every {Duration(policy.ObservationPollInterval)} for " +
               $"{Duration(policy.ObservationPeriod)} of uninterrupted stability. " +
               $"{attempts} {initial} {rearm}";
    }

    private static string Duration(TimeSpan duration)
    {
        int seconds = checked((int)duration.TotalSeconds);
        if (seconds % 3600 == 0) return Unit(seconds / 3600, "hour");
        if (seconds % 60 == 0) return Unit(seconds / 60, "minute");
        return Unit(seconds, "second");
    }

    private static string Unit(int value, string unit) =>
        $"{value} {unit}{(value == 1 ? string.Empty : "s")}";
}

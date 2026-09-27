using System;
using System.Collections.Generic;
using Relight.Core;
using Relight.Storage;

namespace Relight.Services;

internal sealed record RecoveryNotification(string Title, string Message, bool Warning);

/// <summary>One live tray notice of each kind per profile and episode.</summary>
internal sealed class RecoveryNotificationPlanner
{
    private readonly Dictionary<Guid, Guid> _lockoutEpisodes = new();
    private readonly Dictionary<Guid, Guid> _recoveryEpisodes = new();

    public RecoveryNotification? Plan(OperationalEvent entry,
        ProfileConfiguration? profile)
    {
        if (profile is null || entry.ProfileId != profile.Id ||
            entry.EpisodeId is not { } episode) return null;

        if (entry.Kind == OperationalEventKind.LockoutEntered &&
            profile.NotifyOnLockout &&
            Remember(_lockoutEpisodes, profile.Id, episode))
        {
            string reason = entry.AttemptLimit == 0
                ? "Automatic launches are disabled."
                : $"Automatic recovery stopped after {entry.AttemptNumber} of {entry.AttemptLimit} allowed attempts.";
            return new("Relight needs attention",
                $"{profile.Name}: {reason} Still watching for a start.", true);
        }

        if (entry.Kind == OperationalEventKind.ObservationCompleted &&
            entry.Origin == ObservationOrigin.AutomaticLaunch &&
            profile.NotifyOnRecovery &&
            Remember(_recoveryEpisodes, profile.Id, episode))
            return new("Relight recovery verified",
                $"{profile.Name} stayed running through its observation period.", false);

        return null;
    }

    private static bool Remember(Dictionary<Guid, Guid> seen, Guid profileId,
        Guid episode)
    {
        if (seen.TryGetValue(profileId, out Guid previous) && previous == episode)
            return false;
        seen[profileId] = episode;
        return true;
    }
}

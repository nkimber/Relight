using System;
using System.Collections.Generic;
using System.Linq;
using Relight.Core;
using Relight.Windows;

namespace Relight.ViewModels;

public enum TrayIconState { Attention, Recovering, Healthy, Paused }

public sealed record TraySnapshotSummary(
    int Protected, int Observing, int Paused, int Alerts,
    TrayIconState IconState, string Tooltip)
{
    public static TraySnapshotSummary FromProfiles(
        IReadOnlyList<HostedProfileStatus> profiles,
        bool configurationDegraded, bool loggingDegraded)
    {
        ArgumentNullException.ThrowIfNull(profiles);
        int protectedCount = 0;
        int observing = 0;
        int paused = 0;
        int alerts = (configurationDegraded ? 1 : 0) + (loggingDegraded ? 1 : 0);
        bool recovering = false;
        foreach (HostedProfileStatus profile in profiles)
        {
            RecoverySnapshot? recovery = profile.Recovery;
            bool needsAttention = profile.Problem is not null ||
                recovery?.DetectionUnavailable == true ||
                recovery?.LockedOut == true || recovery?.HoldReason is not null;
            if (needsAttention) alerts++;
            if (recovery?.Paused == true) paused++;
            if (!profile.AutomaticActionsAllowed || recovery is not
                { Enabled: true, Paused: false, LockedOut: false,
                  DetectionUnavailable: false, HoldReason: null })
                continue;
            protectedCount++;
            if (recovery.State == RecoveryState.Observing) observing++;
            if (recovery.State is RecoveryState.Starting or RecoveryState.Observing or
                RecoveryState.RetryWaiting) recovering = true;
        }
        TrayIconState icon = alerts > 0 ? TrayIconState.Attention
            : recovering ? TrayIconState.Recovering
            : protectedCount > 0 ? TrayIconState.Healthy
            : TrayIconState.Paused;
        string tooltip = $"Relight: {protectedCount} protected, {observing} observing, " +
            $"{paused} paused, {alerts} alerts";
        return new(protectedCount, observing, paused, alerts, icon, tooltip);
    }
}

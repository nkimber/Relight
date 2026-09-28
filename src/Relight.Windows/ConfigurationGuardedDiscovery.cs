using Relight.Core;
using Relight.Engine;
using Relight.Storage;

namespace Relight.Windows;

/// <summary>
/// Prevents a shared-session host from treating its cached profile as launch or
/// termination authority after another sign-in changes shared configuration.
/// </summary>
internal sealed class ConfigurationGuardedDiscovery(
    IProcessDiscovery inner,
    ConfigurationStore store,
    Func<StoredConfiguration?> activeConfiguration) : IProcessDiscovery
{
    public async Task<Detection> DetectAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (CheckConfiguration(store, activeConfiguration()) is { } before)
            return Detection.Unavailable(before);
        Detection result = await inner.DetectAsync(cancellationToken).ConfigureAwait(false);
        return CheckConfiguration(store, activeConfiguration()) is { } after
            ? Detection.Unavailable(after) : result;
    }

    internal static string? CheckConfiguration(ConfigurationStore store,
        StoredConfiguration? active)
    {
        if (active is null || !active.AutomaticActionsAllowed)
            return "Shared configuration is unavailable or degraded.";
        try
        {
            StoredConfiguration current = store.Load();
            if (!current.AutomaticActionsAllowed || current.Revision != active.Revision ||
                !string.Equals(current.ContentHash, active.ContentHash,
                    StringComparison.Ordinal))
                return "Shared configuration changed in another session; recovery is suspended until reconciled.";
        }
        catch (ConfigurationUnavailableException error)
        {
            return $"Shared configuration cannot be verified: {error.Message}";
        }
        return null;
    }
}

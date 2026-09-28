using Relight.Engine;
using Relight.Storage;

namespace Relight.Windows;

/// <summary>Keeps shared configuration stable through an explicit stop action.</summary>
internal sealed class ConfigurationGuardedStopper(
    IProcessStopper inner,
    ConfigurationStore store,
    Func<StoredConfiguration?> activeConfiguration) : IProcessStopper
{
    public Task<TargetStopResult> TryGracefulCloseAsync(string selectedIdentity,
        TimeSpan timeout, CancellationToken cancellationToken = default) =>
        GuardAsync(() => inner.TryGracefulCloseAsync(selectedIdentity, timeout,
            cancellationToken), cancellationToken);

    public Task<TargetStopResult> ForceCloseAsync(string selectedIdentity,
        CancellationToken cancellationToken = default) =>
        GuardAsync(() => inner.ForceCloseAsync(selectedIdentity, cancellationToken),
            cancellationToken);

    private async Task<TargetStopResult> GuardAsync(Func<Task<TargetStopResult>> action,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            StoredConfiguration expected = activeConfiguration() ??
                throw new ConfigurationUnavailableException("Shared configuration is unavailable.");
            using (store.AcquireTargetActionLease(expected))
            {
                cancellationToken.ThrowIfCancellationRequested();
                return await action().ConfigureAwait(false);
            }
        }
        catch (Exception error) when (error is ConfigurationUnavailableException or
            StaleConfigurationException)
        {
            return new(TargetStopOutcome.Unavailable,
                $"Shared configuration cannot authorize this stop: {error.Message}");
        }
    }
}

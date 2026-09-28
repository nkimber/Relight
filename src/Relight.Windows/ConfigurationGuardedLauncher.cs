using Relight.Engine;
using Relight.Storage;

namespace Relight.Windows;

/// <summary>Serializes shared configuration saves with the final launch dispatch.</summary>
internal sealed class ConfigurationGuardedLauncher(
    IProcessLauncher inner,
    ConfigurationStore store,
    Func<StoredConfiguration?> activeConfiguration) : IProcessLauncher
{
    public async Task LaunchAsync(Guid operationId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        StoredConfiguration expected = activeConfiguration() ??
            throw new ConfigurationUnavailableException("Shared configuration is unavailable.");
        using (store.AcquireTargetActionLease(expected))
        {
            cancellationToken.ThrowIfCancellationRequested();
            await inner.LaunchAsync(operationId, cancellationToken).ConfigureAwait(false);
        }
    }
}

using Relight.Engine;
using Relight.Storage;
using Relight.Windows;

namespace Relight.Core.Tests;

public sealed class ConfigurationGuardedLauncherTests
{
    [Fact]
    public async Task Dispatch_holds_revision_until_launcher_returns()
    {
        string directory = Path.Combine(Path.GetTempPath(), $"relight-launch-lease-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            var store = new ConfigurationStore(directory);
            StoredConfiguration active = store.Initialize(RelightConfiguration.Empty);
            var inner = new WaitingLauncher();
            var guarded = new ConfigurationGuardedLauncher(inner, store, () => active);

            Task launch = guarded.LaunchAsync(Guid.NewGuid(), CancellationToken.None);
            await inner.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Throws<ConfigurationUnavailableException>(() =>
            {
                new ConfigurationStore(directory).Save(active, RelightConfiguration.Empty);
            });
            inner.Release.SetResult();
            await launch;

            store.Save(active, RelightConfiguration.Empty);
            await Assert.ThrowsAsync<StaleConfigurationException>(() =>
                guarded.LaunchAsync(Guid.NewGuid(), CancellationToken.None));
            Assert.Equal(1, inner.DispatchCount);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    private sealed class WaitingLauncher : IProcessLauncher
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int DispatchCount { get; private set; }

        public async Task LaunchAsync(Guid operationId, CancellationToken cancellationToken)
        {
            DispatchCount++;
            Entered.SetResult();
            await Release.Task.WaitAsync(cancellationToken);
        }
    }
}

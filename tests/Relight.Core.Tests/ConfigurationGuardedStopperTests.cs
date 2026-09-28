using Relight.Engine;
using Relight.Storage;
using Relight.Windows;

namespace Relight.Core.Tests;

public sealed class ConfigurationGuardedStopperTests
{
    [Fact]
    public async Task Stale_configuration_blocks_stop_and_active_stop_blocks_save()
    {
        string directory = Path.Combine(Path.GetTempPath(), $"relight-stop-lease-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            var store = new ConfigurationStore(directory);
            StoredConfiguration active = store.Initialize(RelightConfiguration.Empty);
            var inner = new WaitingStopper();
            var guarded = new ConfigurationGuardedStopper(inner, store, () => active);

            Task<TargetStopResult> stop = guarded.TryGracefulCloseAsync("selected",
                TimeSpan.FromSeconds(5));
            await inner.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(active.ContentHash, store.Load().ContentHash);
            Assert.Throws<ConfigurationUnavailableException>(() =>
            {
                store.Save(active, RelightConfiguration.Empty);
            });
            inner.Release.SetResult();
            Assert.Equal(TargetStopOutcome.Stopped, (await stop).Outcome);

            store.Save(active, RelightConfiguration.Empty);
            TargetStopResult stale = await guarded.ForceCloseAsync("selected");
            Assert.Equal(TargetStopOutcome.Unavailable, stale.Outcome);
            Assert.Equal(1, inner.Calls);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    private sealed class WaitingStopper : IProcessStopper
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Calls { get; private set; }

        public async Task<TargetStopResult> TryGracefulCloseAsync(string selectedIdentity,
            TimeSpan timeout, CancellationToken cancellationToken = default)
        {
            Calls++;
            Entered.SetResult();
            await Release.Task.WaitAsync(cancellationToken);
            return new(TargetStopOutcome.Stopped);
        }

        public Task<TargetStopResult> ForceCloseAsync(string selectedIdentity,
            CancellationToken cancellationToken = default)
        {
            Calls++;
            return Task.FromResult(new TargetStopResult(TargetStopOutcome.Stopped));
        }
    }
}

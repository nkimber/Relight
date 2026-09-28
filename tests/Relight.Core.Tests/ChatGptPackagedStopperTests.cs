using System.Diagnostics;
using Relight.Core;
using Relight.Engine;
using Relight.Windows;

namespace Relight.Core.Tests;

public sealed class ChatGptPackagedStopperTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Changed_or_unavailable_identity_cannot_trigger_a_close(bool force)
    {
        string selected = $"0|{ChatGptPackagedDiscovery.PackageFamilyName}|2147483647|1";
        var changed = new ChatGptPackagedStopper(new FixedDiscovery(
            Detection.Present(selected + "-replacement")));
        TargetStopResult result = force
            ? await changed.ForceCloseAsync(selected)
            : await changed.TryGracefulCloseAsync(selected, TimeSpan.FromSeconds(1));
        Assert.Equal(TargetStopOutcome.IdentityChanged, result.Outcome);

        var unavailable = new ChatGptPackagedStopper(new FixedDiscovery(
            Detection.Unavailable("Ambiguous main process")));
        result = force
            ? await unavailable.ForceCloseAsync(selected)
            : await unavailable.TryGracefulCloseAsync(selected, TimeSpan.FromSeconds(1));
        Assert.Equal(TargetStopOutcome.Unavailable, result.Outcome);
    }

    [Fact]
    public async Task Forged_selected_identity_is_rejected_before_process_access()
    {
        string forged = "0|OpenAI.ChatGPT-Desktop_2p2nqsd0c76g0|2147483647|1";
        var stopper = new ChatGptPackagedStopper(new FixedDiscovery(
            Detection.Present(forged)));
        TargetStopResult result = await stopper.ForceCloseAsync(forged);
        Assert.Equal(TargetStopOutcome.IdentityChanged, result.Outcome);
    }

    [Fact]
    public async Task Disappeared_selected_pid_cannot_be_force_closed()
    {
        using Process current = Process.GetCurrentProcess();
        string selected = $"{current.SessionId}|{ChatGptPackagedDiscovery.PackageFamilyName}|2147483647|1";
        var stopper = new ChatGptPackagedStopper(new FixedDiscovery(
            Detection.Present(selected)));

        TargetStopResult result = await stopper.ForceCloseAsync(selected);

        Assert.Equal(TargetStopOutcome.IdentityChanged, result.Outcome);
    }

    private sealed class FixedDiscovery(Detection detection) : IProcessDiscovery
    {
        public Task<Detection> DetectAsync(CancellationToken cancellationToken) =>
            Task.FromResult(detection);
    }
}

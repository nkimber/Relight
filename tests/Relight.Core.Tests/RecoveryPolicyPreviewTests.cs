using Relight.Core;
using Relight.ViewModels;

namespace Relight.Core.Tests;

public sealed class RecoveryPolicyPreviewTests
{
    [Fact]
    public void Default_preview_explains_timing_limit_and_initial_wait()
    {
        string preview = RecoveryPolicyPreview.Describe(RecoveryPolicy.Default);

        Assert.Contains("Check every 2 minutes", preview);
        Assert.Contains("every 5 seconds for 10 minutes", preview);
        Assert.Contains("Try up to 3 per episode", preview);
        Assert.Contains("waits for your first launch", preview);
        Assert.Contains("stable external start can rearm", preview);
    }

    [Fact]
    public void Zero_attempts_and_initial_auto_start_are_distinct()
    {
        RecoveryPolicy policy = RecoveryPolicy.Default with
        {
            MaximumAutomaticAttempts = 0,
            StartAutomaticallyWhenInitiallyAbsent = true,
            RearmAfterStableExternalStart = false
        };

        string preview = RecoveryPolicyPreview.Describe(policy);

        Assert.Contains("Automatic starts are off", preview);
        Assert.Contains("detection and explicit Start now remain available", preview);
        Assert.Contains("waits for an explicit start", preview);
        Assert.DoesNotContain("may start it automatically", preview);
        Assert.Contains("does not clear lockout", preview);
        Assert.DoesNotContain("Try up to", preview);
    }
}

using System;
using System.Threading.Tasks;

namespace Relight.Services;

/// <summary>Allow cleanup to drain, without letting a non-cancellable adapter prevent Exit.</summary>
internal static class MonitoringShutdown
{
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    public static async Task DrainAsync(Task? monitoring, TimeProvider? timeProvider = null)
    {
        if (monitoring is not null)
            await monitoring.WaitAsync(Timeout, timeProvider ?? TimeProvider.System);
    }
}

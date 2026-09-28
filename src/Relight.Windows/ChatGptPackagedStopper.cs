using System.ComponentModel;
using System.Diagnostics;
using Relight.Core;
using Relight.Engine;

namespace Relight.Windows;

/// <summary>
/// Explicit-only stopper for the selected ChatGPT package's single main process.
/// Discovery verifies package family, owner, session and helper classification;
/// the process is then rechecked by PID and start time before any close request.
/// </summary>
public sealed class ChatGptPackagedStopper : IProcessStopper
{
    private readonly IProcessDiscovery _discovery;
    private readonly int _sessionId;

    public ChatGptPackagedStopper() : this(new ChatGptPackagedDiscovery()) { }

    internal ChatGptPackagedStopper(IProcessDiscovery discovery)
    {
        _discovery = discovery ?? throw new ArgumentNullException(nameof(discovery));
        using Process current = Process.GetCurrentProcess();
        _sessionId = current.SessionId;
    }

    public async Task<TargetStopResult> TryGracefulCloseAsync(string selectedIdentity,
        TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        if (timeout <= TimeSpan.Zero || timeout > TimeSpan.FromMinutes(5))
            throw new ArgumentOutOfRangeException(nameof(timeout));
        TargetStopResult? check = await CheckSelectedAsync(selectedIdentity,
            cancellationToken).ConfigureAwait(false);
        if (check is not null) return check;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            using Process process = OpenSelected(selectedIdentity);
            if (process.HasExited) return new(TargetStopOutcome.AlreadyAbsent);
            cancellationToken.ThrowIfCancellationRequested();
            if (!process.CloseMainWindow())
                return new(TargetStopOutcome.NeedsForceChoice,
                    "The selected ChatGPT main process has no responsive window for graceful close.");
            using var timeoutCancellation = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken);
            timeoutCancellation.CancelAfter(timeout);
            try
            {
                await process.WaitForExitAsync(timeoutCancellation.Token)
                    .ConfigureAwait(false);
                return new(TargetStopOutcome.Stopped);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                return new(TargetStopOutcome.NeedsForceChoice,
                    "ChatGPT did not exit before the graceful-close timeout.");
            }
        }
        catch (SelectedProcessChangedException)
        {
            return new(TargetStopOutcome.IdentityChanged,
                "The selected ChatGPT main process changed before it could be closed.");
        }
        catch (Exception error) when (error is Win32Exception or
            UnauthorizedAccessException or InvalidOperationException)
        {
            return new(TargetStopOutcome.Unavailable,
                $"The selected ChatGPT main process could not be closed safely: {error.Message}");
        }
    }

    public async Task<TargetStopResult> ForceCloseAsync(string selectedIdentity,
        CancellationToken cancellationToken = default)
    {
        TargetStopResult? check = await CheckSelectedAsync(selectedIdentity,
            cancellationToken).ConfigureAwait(false);
        if (check is not null) return check;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            using Process process = OpenSelected(selectedIdentity);
            if (process.HasExited) return new(TargetStopOutcome.AlreadyAbsent);
            cancellationToken.ThrowIfCancellationRequested();
            process.Kill(entireProcessTree: false);
            using var timeoutCancellation = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken);
            timeoutCancellation.CancelAfter(TimeSpan.FromSeconds(15));
            try
            {
                await process.WaitForExitAsync(timeoutCancellation.Token)
                    .ConfigureAwait(false);
                return new(TargetStopOutcome.Stopped);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                return new(TargetStopOutcome.Unavailable,
                    "Force close was requested, but ChatGPT main-process exit is not yet verified.");
            }
        }
        catch (SelectedProcessChangedException)
        {
            return new(TargetStopOutcome.IdentityChanged,
                "The selected ChatGPT main process changed before force close.");
        }
        catch (Exception error) when (error is Win32Exception or
            UnauthorizedAccessException or InvalidOperationException)
        {
            return new(TargetStopOutcome.Unavailable,
                $"The selected ChatGPT main process could not be force closed: {error.Message}");
        }
    }

    private async Task<TargetStopResult?> CheckSelectedAsync(string selectedIdentity,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(selectedIdentity))
            throw new ArgumentException("A verified instance identity is required.",
                nameof(selectedIdentity));
        Detection found = await _discovery.DetectAsync(cancellationToken)
            .ConfigureAwait(false);
        return found.Kind switch
        {
            DetectionKind.Absent => new(TargetStopOutcome.AlreadyAbsent),
            DetectionKind.Unavailable => new(TargetStopOutcome.Unavailable,
                found.Reason ?? "ChatGPT identity is unavailable."),
            DetectionKind.Present when !string.Equals(found.Identity, selectedIdentity,
                StringComparison.Ordinal) => new(TargetStopOutcome.IdentityChanged,
                "The selected ChatGPT instance is no longer the verified main process."),
            _ => null
        };
    }

    private Process OpenSelected(string selectedIdentity)
    {
        string[] parts = selectedIdentity.Split('|');
        if (parts.Length != 4 ||
            !int.TryParse(parts[0], out int sessionId) ||
            !int.TryParse(parts[2], out int processId) ||
            !long.TryParse(parts[3], out long startedTicks) ||
            sessionId != _sessionId || processId <= 0 || startedTicks <= 0 ||
            !string.Equals(parts[1], ChatGptPackagedDiscovery.PackageFamilyName,
                StringComparison.Ordinal))
            throw new SelectedProcessChangedException();
        Process process;
        try { process = Process.GetProcessById(processId); }
        catch (ArgumentException) { throw new SelectedProcessChangedException(); }
        try
        {
            if (process.HasExited || process.SessionId != sessionId ||
                process.StartTime.ToUniversalTime().Ticks != startedTicks ||
                !string.Equals(process.ProcessName, "ChatGPT",
                    StringComparison.OrdinalIgnoreCase))
                throw new SelectedProcessChangedException();
            return process;
        }
        catch
        {
            process.Dispose();
            throw;
        }
    }

    private sealed class SelectedProcessChangedException : Exception { }
}

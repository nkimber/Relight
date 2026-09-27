using System.ComponentModel;
using System.Diagnostics;
using Relight.Core;
using Relight.Engine;

namespace Relight.Windows;

/// <summary>
/// Explicit-only Windows stop adapter. Each call rediscovers the selected target
/// and checks PID/start time again on its process handle before any close request.
/// A caller must request force close separately after a user choice.
/// </summary>
public sealed class ExecutableStopper : IProcessStopper
{
    private readonly ExecutableTarget _target;
    private readonly ExecutableDiscovery _discovery;

    public ExecutableStopper(ExecutableTarget target)
    {
        target.Validate();
        _target = target;
        _discovery = new(target);
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
            using Process process = OpenSelected(selectedIdentity);
            if (process.HasExited) return new(TargetStopOutcome.AlreadyAbsent);
            if (!process.CloseMainWindow())
                return new(TargetStopOutcome.NeedsForceChoice,
                    "The selected process has no responsive main window for graceful close.");
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
                    "The selected process did not exit before the graceful-close timeout.");
            }
        }
        catch (SelectedProcessChangedException)
        {
            return new(TargetStopOutcome.IdentityChanged,
                "The selected process changed before it could be closed.");
        }
        catch (Exception error) when (error is Win32Exception or
            UnauthorizedAccessException or InvalidOperationException)
        {
            return new(TargetStopOutcome.Unavailable,
                $"The selected process could not be closed safely: {error.Message}");
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
            using Process process = OpenSelected(selectedIdentity);
            if (process.HasExited) return new(TargetStopOutcome.AlreadyAbsent);
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
                    "Force close was requested, but the selected process exit is not yet verified.");
            }
        }
        catch (SelectedProcessChangedException)
        {
            return new(TargetStopOutcome.IdentityChanged,
                "The selected process changed before force close.");
        }
        catch (Exception error) when (error is Win32Exception or
            UnauthorizedAccessException or InvalidOperationException)
        {
            return new(TargetStopOutcome.Unavailable,
                $"The selected process could not be force closed: {error.Message}");
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
                found.Reason ?? "Target identity is unavailable."),
            DetectionKind.Present when !string.Equals(found.Identity, selectedIdentity,
                StringComparison.Ordinal) => new(TargetStopOutcome.IdentityChanged,
                "The selected instance is no longer the verified matching instance."),
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
            processId <= 0 || startedTicks <= 0 ||
            !string.Equals(parts[1], _target.CanonicalPath,
                StringComparison.OrdinalIgnoreCase))
            throw new SelectedProcessChangedException();
        Process process;
        try { process = Process.GetProcessById(processId); }
        catch (ArgumentException) { throw new SelectedProcessChangedException(); }
        try
        {
            string? actualPath = process.MainModule?.FileName;
            if (process.HasExited || process.SessionId != sessionId ||
                process.StartTime.ToUniversalTime().Ticks != startedTicks ||
                actualPath is null ||
                !string.Equals(Path.GetFullPath(actualPath),
                    _target.CanonicalPath, StringComparison.OrdinalIgnoreCase))
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

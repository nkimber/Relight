using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using Relight.Core;
using Relight.Engine;

namespace Relight.Windows;

/// <summary>
/// Speeds up reconciliation for a verified executable instance. A missed event
/// never establishes absence; the coordinator still performs normal discovery.
/// </summary>
internal sealed class ExecutableExitSignaledDiscovery(
    ExecutableDiscovery inner, Action requestPoll) : IProcessDiscovery, IDisposable
{
    private readonly object _sync = new();
    private Process? _watched;
    private string? _identity;
    private string? _signaledIdentity;
    private bool _disposed;

    public async Task<Detection> DetectAsync(CancellationToken cancellationToken)
    {
        Detection result = await inner.DetectAsync(cancellationToken).ConfigureAwait(false);
        if (result.Kind == DetectionKind.Present && result.Identity is { } identity)
            Watch(identity);
        else if (result.Kind == DetectionKind.Absent)
            ClearWatch();
        return result;
    }

    private void Watch(string identity)
    {
        string[] parts = identity.Split('|');
        if (parts.Length != 4 ||
            !int.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture,
                out int pid) ||
            !long.TryParse(parts[3], NumberStyles.None, CultureInfo.InvariantCulture,
                out long startedTicks)) return;

        Process? previous;
        bool reconcile = false;
        lock (_sync)
        {
            if (_disposed || _signaledIdentity == identity ||
                (_identity == identity && _watched is not null)) return;
            previous = _watched;
            _watched = null;
            _identity = null;
            _signaledIdentity = null;
            Process? candidate = null;
            try
            {
                candidate = Process.GetProcessById(pid);
                if (candidate.StartTime.ToUniversalTime().Ticks != startedTicks)
                {
                    reconcile = true;
                }
                else
                {
                    candidate.Exited += OnExited;
                    candidate.EnableRaisingEvents = true;
                    _watched = candidate;
                    _identity = identity;
                    reconcile = candidate.HasExited;
                    candidate = null;
                }
            }
            catch (Exception error) when (error is ArgumentException or
                InvalidOperationException or Win32Exception or UnauthorizedAccessException)
            {
                // The process disappeared or could not be watched after a valid lookup.
                // Polling remains authoritative and will report the current state.
                reconcile = true;
            }
            finally { candidate?.Dispose(); }
            if (reconcile) _signaledIdentity = identity;
        }
        Release(previous);
        if (reconcile) requestPoll();
    }

    private void OnExited(object? sender, EventArgs args)
    {
        Process? ended = null;
        lock (_sync)
        {
            if (!_disposed && ReferenceEquals(sender, _watched))
            {
                ended = _watched;
                _signaledIdentity = _identity;
                _watched = null;
                _identity = null;
            }
        }
        if (ended is null) return;
        Release(ended);
        requestPoll();
    }

    private void ClearWatch()
    {
        Process? previous;
        lock (_sync)
        {
            previous = _watched;
            _watched = null;
            _identity = null;
            _signaledIdentity = null;
        }
        Release(previous);
    }

    private void Release(Process? process)
    {
        if (process is null) return;
        process.Exited -= OnExited;
        process.Dispose();
    }

    public void Dispose()
    {
        lock (_sync) _disposed = true;
        ClearWatch();
    }
}

using Relight.Core;
using Relight.Engine;
using Relight.Storage;

namespace Relight.Windows;

/// <summary>
/// Prevents a shared-session host from treating its cached profile as launch or
/// termination authority after another sign-in changes shared configuration.
/// </summary>
internal sealed class ConfigurationGuardedDiscovery(
    IProcessDiscovery inner,
    ConfigurationStore store,
    Func<StoredConfiguration?> activeConfiguration) : IProcessDiscovery
{
    public async Task<Detection> DetectAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (CheckConfiguration(store, activeConfiguration(), out var diagnostic) is { } before)
            return Detection.Unavailable(before, DetectionFailureKind.ConfigurationChanged,
                diagnostic: diagnostic);
        Detection result = await inner.DetectAsync(cancellationToken).ConfigureAwait(false);
        return CheckConfiguration(store, activeConfiguration(), out diagnostic) is { } after
            ? Detection.Unavailable(after, DetectionFailureKind.ConfigurationChanged,
                diagnostic: diagnostic) : result;
    }

    internal static string? CheckConfiguration(ConfigurationStore store,
        StoredConfiguration? active)
        => CheckConfiguration(store, active, out _);

    private static string? CheckConfiguration(ConfigurationStore store,
        StoredConfiguration? active, out DetectionDiagnostic? diagnostic)
    {
        diagnostic = new(DetectionDiagnosticCode.ConfigurationUnavailable,
            "ConfigurationGuardedDiscovery", "Configuration",
            ActiveConfigurationRevision: active?.Revision);
        if (active is null || !active.AutomaticActionsAllowed)
            return "Shared configuration is unavailable or degraded.";
        try
        {
            StoredConfiguration current = store.Load();
            diagnostic = diagnostic with { Code = DetectionDiagnosticCode.ConfigurationChanged,
                DiskConfigurationRevision = current.Revision };
            if (!current.AutomaticActionsAllowed || current.Revision != active.Revision ||
                !string.Equals(current.ContentHash, active.ContentHash,
                    StringComparison.Ordinal))
                return "Shared configuration changed in another session; recovery is suspended until reconciled.";
        }
        catch (ConfigurationUnavailableException error)
        {
            diagnostic = diagnostic with { Code = DetectionDiagnosticCode.ConfigurationReadFailed,
                ExceptionType = error.GetType().Name, HResult = error.HResult };
            return $"Shared configuration cannot be verified: {error.Message}";
        }
        diagnostic = null;
        return null;
    }
}

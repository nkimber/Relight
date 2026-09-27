using Relight.Core;
using Relight.Engine;
using Relight.Storage;

namespace Relight.Windows;

public sealed record HostedProfileStatus(
    Guid Id,
    string Name,
    bool Monitoring,
    bool AutomaticActionsAllowed,
    Detection? Detection,
    RecoverySnapshot? Recovery,
    string? Problem);

/// <summary>
/// Composes existing executable profiles without inventing recovery state.
/// A missing/corrupt state or invalid configuration never becomes a fresh
/// attempt budget. Packaged targets remain unavailable until their adapter is
/// validated.
/// </summary>
public sealed class RecoveryApplicationHost : IAsyncDisposable
{
    private readonly ConfigurationStore _configurationStore;
    private readonly RecoveryStateStore _stateStore;
    private OperationalEventJournal? _journal;
    private QueuedEventRecorder? _recorder;
    private readonly BoundedLaunchGate _launchGate = new();
    private readonly RecoveryScheduler _scheduler;
    private readonly IMonotonicClock _clock;
    private readonly Dictionary<Guid, HostedProfileStatus> _statuses = new();
    private bool _disposed;

    private RecoveryApplicationHost(string dataDirectory, IMonotonicClock clock)
    {
        _configurationStore = new(dataDirectory);
        _stateStore = new(dataDirectory);
        _clock = clock;
        _scheduler = new(clock);
    }

    public static string DefaultDataDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Relight");

    public StoredConfiguration? Configuration { get; private set; }
    public string? ConfigurationProblem { get; private set; }

    public static async Task<RecoveryApplicationHost> OpenAsync(
        string? dataDirectory = null, IMonotonicClock? clock = null,
        CancellationToken cancellationToken = default)
    {
        var host = new RecoveryApplicationHost(dataDirectory ?? DefaultDataDirectory,
            clock ?? new StopwatchClock());
        try
        {
            await host.InitializeAsync(dataDirectory ?? DefaultDataDirectory, cancellationToken)
                .ConfigureAwait(false);
            return host;
        }
        catch
        {
            await host.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private async Task InitializeAsync(string dataDirectory, CancellationToken cancellationToken)
    {
        string path = Path.Combine(Path.GetFullPath(dataDirectory), "configuration.json");
        try
        {
            if (!File.Exists(path) && !File.Exists(path + ".bak"))
            {
                try { Configuration = _configurationStore.Initialize(RelightConfiguration.Empty); }
                catch (InvalidOperationException)
                {
                    // Another signed-in session may have initialized it first.
                    Configuration = _configurationStore.Load();
                }
            }
            else Configuration = _configurationStore.Load();
            ConfigurationProblem = Configuration.Diagnostic;
        }
        catch (ConfigurationUnavailableException error)
        {
            ConfigurationProblem = error.Message;
            InitializeLogging(dataDirectory, GlobalConfiguration.Default);
            return;
        }

        InitializeLogging(dataDirectory, Configuration.Configuration.Settings);
        _recorder!.TryRecord(new(DateTimeOffset.UtcNow, Guid.NewGuid(),
            EventSeverity.Information, OperationalEventKind.Startup));

        foreach (ProfileConfiguration profile in Configuration.Configuration.Profiles)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!profile.Enabled)
            {
                _statuses[profile.Id] = new(profile.Id, profile.Name, false, false,
                    null, null, null);
                continue;
            }
            if (profile.Target.Kind != TargetKind.Executable)
            {
                _statuses[profile.Id] = new(profile.Id, profile.Name, false, false, null, null,
                    "Installed-application activation is not available yet.");
                continue;
            }

            ExecutableTarget target;
            try
            {
                target = new(profile.Target.Identity, profile.Target.Arguments,
                    profile.Target.WorkingDirectory, profile.Target.RequiredArgument,
                    profile.Target.ExcludedArgument);
                target.Validate();
            }
            catch (ArgumentException error)
            {
                _statuses[profile.Id] = new(profile.Id, profile.Name, false, false, null, null,
                    $"Executable identity is invalid: {error.Message}");
                continue;
            }

            ExecutableDiscovery discovery;
            try { discovery = new(target); }
            catch (Exception error) when (error is InvalidOperationException or ArgumentException)
            {
                _statuses[profile.Id] = new(profile.Id, profile.Name, false, false,
                    null, null, $"Process discovery is unavailable: {error.Message}");
                continue;
            }
            if (!Configuration.AutomaticActionsAllowed || !File.Exists(target.CanonicalPath))
            {
                string problem = !Configuration.AutomaticActionsAllowed
                    ? "Configuration is degraded; automatic actions are suspended."
                    : "Target executable is missing; automatic actions are suspended.";
                _statuses[profile.Id] = await PassiveStatus(profile, discovery, problem,
                    cancellationToken).ConfigureAwait(false);
                continue;
            }

            try
            {
                ProfileCoordinator coordinator = ProfileCoordinator.OpenExisting(profile.Id,
                    profile.Policy, _stateStore, discovery, new ExecutableLauncher(target),
                    _clock, _launchGate, _recorder);
                _scheduler.Add(profile.Id, coordinator, profile.Policy);
                _statuses[profile.Id] = new(profile.Id, profile.Name, true, true,
                    null, coordinator.Snapshot, null);
            }
            catch (Exception error) when (error is IOException or InvalidOperationException or ArgumentException)
            {
                _statuses[profile.Id] = await PassiveStatus(profile, discovery,
                    $"Recovery state is unavailable; automatic actions are suspended. {error.Message}",
                    cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private void InitializeLogging(string dataDirectory, GlobalConfiguration settings)
    {
        _journal = new(dataDirectory, settings);
        _recorder = new(_journal);
    }

    private async Task<HostedProfileStatus> PassiveStatus(ProfileConfiguration profile,
        IProcessDiscovery discovery, string problem, CancellationToken cancellationToken)
    {
        Detection detection;
        try { detection = await discovery.DetectAsync(cancellationToken).ConfigureAwait(false); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception error) { detection = Detection.Unavailable(error.Message); }
        _scheduler.AddPassive(profile.Id, discovery, profile.Policy.LockoutDiscoveryInterval,
            detection);
        return new(profile.Id, profile.Name, true, false, detection, null, problem);
    }

    public IReadOnlyList<HostedProfileStatus> GetProfiles() =>
        _statuses.Values.Select(status =>
        {
            (CoordinatorResult? Result, string? Error)? last = _scheduler.GetLast(status.Id);
            if (last is null)
                return status with
                {
                    Detection = _scheduler.GetPassiveLast(status.Id) ?? status.Detection
                };
            return status with
            {
                Recovery = last.Value.Result?.Snapshot ?? status.Recovery,
                AutomaticActionsAllowed = status.AutomaticActionsAllowed &&
                    !(last.Value.Result?.StorageDegraded ?? false),
                Problem = last.Value.Error ?? last.Value.Result?.Error ?? status.Problem
            };
        }).ToArray();

    public IReadOnlyDictionary<Guid, Task> Pulse() => _scheduler.Pulse();

    public void RequestImmediate(Guid profileId) => _scheduler.RequestImmediate(profileId);

    public Task<EventRecorderStatus?> GetLoggingStatusAsync(
        CancellationToken cancellationToken = default) =>
        _recorder is null
            ? Task.FromResult<EventRecorderStatus?>(null)
            : ReadLoggingStatusAsync(_recorder, cancellationToken);

    private static async Task<EventRecorderStatus?> ReadLoggingStatusAsync(
        QueuedEventRecorder recorder, CancellationToken cancellationToken) =>
        await recorder.GetStatusAsync(cancellationToken).ConfigureAwait(false);

    public Task RunAsync(CancellationToken cancellationToken = default) =>
        _scheduler.RunAsync(cancellationToken);

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        _recorder?.TryRecord(new(DateTimeOffset.UtcNow, Guid.NewGuid(),
            EventSeverity.Information, OperationalEventKind.Shutdown));
        await _scheduler.DisposeAsync().ConfigureAwait(false);
        if (_recorder is not null) await _recorder.DisposeAsync().ConfigureAwait(false);
        _journal?.Dispose();
        _launchGate.Dispose();
    }
}

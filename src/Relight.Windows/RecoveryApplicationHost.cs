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
    private readonly Dictionary<Guid, ProfileCoordinator> _coordinators = new();
    private readonly HashSet<Task> _activeCommands = [];
    private readonly object _statusSync = new();
    private readonly SemaphoreSlim _changes = new(1, 1);
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
                _coordinators.Add(profile.Id, coordinator);
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

    public IReadOnlyList<HostedProfileStatus> GetProfiles()
    {
        (HostedProfileStatus Status, ProfileCoordinator? Coordinator)[] statuses;
        lock (_statusSync)
            statuses = _statuses.Values.Select(status =>
                (status, _coordinators.GetValueOrDefault(status.Id))).ToArray();
        return statuses.Select(entry =>
        {
            HostedProfileStatus status = entry.Status;
            ProfileCoordinator? coordinator = entry.Coordinator;
            (CoordinatorResult? Result, string? Error)? last = _scheduler.GetLast(status.Id);
            if (last is null)
                return status with
                {
                    Detection = _scheduler.GetPassiveLast(status.Id) ?? status.Detection,
                    Recovery = coordinator?.Snapshot ?? status.Recovery,
                    AutomaticActionsAllowed = status.AutomaticActionsAllowed &&
                        !(coordinator?.StorageDegraded ?? false),
                    Problem = coordinator?.StorageError ?? status.Problem
                };
            return status with
            {
                Recovery = coordinator?.Snapshot ?? last.Value.Result?.Snapshot ?? status.Recovery,
                AutomaticActionsAllowed = status.AutomaticActionsAllowed &&
                    !(coordinator?.StorageDegraded ?? last.Value.Result?.StorageDegraded ?? false),
                Problem = last.Value.Error ?? coordinator?.StorageError ??
                    last.Value.Result?.Error ?? status.Problem
            };
        }).ToArray();
    }

    public static async Task<Detection> InspectExecutableAsync(string executablePath,
        CancellationToken cancellationToken = default)
    {
        var target = new ExecutableTarget(executablePath, []);
        target.Validate();
        if (!File.Exists(target.CanonicalPath))
            return Detection.Unavailable("The executable file does not exist.");
        return await new ExecutableDiscovery(target).DetectAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Creates a new executable profile with a durable initial ledger before
    /// publishing enabled configuration. A failed configuration save may leave
    /// an unreferenced ledger; it is intentionally preserved rather than
    /// risking reuse of its identity or a fresh budget.
    /// </summary>
    public async Task<Guid> RegisterExecutableAsync(string name, string executablePath,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length > 100)
            throw new ArgumentException("Choose a name of 1–100 characters.", nameof(name));
        var target = new ExecutableTarget(executablePath, []);
        target.Validate();
        if (!File.Exists(target.CanonicalPath))
            throw new FileNotFoundException("The executable file does not exist.", target.CanonicalPath);
        var discovery = new ExecutableDiscovery(target);
        Detection detected = await discovery.DetectAsync(cancellationToken).ConfigureAwait(false);
        if (detected.Kind == DetectionKind.Unavailable)
            throw new InvalidOperationException($"Target identity cannot be verified: {detected.Reason}");

        await _changes.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_disposed) throw new ObjectDisposedException(nameof(RecoveryApplicationHost));
            StoredConfiguration current = Configuration ??
                throw new ConfigurationUnavailableException("Configuration is unavailable.");
            if (!current.AutomaticActionsAllowed)
                throw new ConfigurationUnavailableException("Repair configuration before adding a profile.");

            Guid id = Guid.NewGuid();
            RecoveryPolicy policy = RecoveryPolicy.Default;
            var profile = new ProfileConfiguration(id, name.Trim(), true,
                new(TargetKind.Executable, target.CanonicalPath, []), policy);
            var updated = current.Configuration with
            {
                Profiles = [.. current.Configuration.Profiles, profile]
            };
            // Validate overlaps and the on-disk revision before reserving a new
            // profile ID. The final Save repeats both checks atomically.
            ConfigurationStore.ValidateConfiguration(updated);
            ProfileCoordinator coordinator = await Task.Run(() =>
                ProfileCoordinator.CreateNew(id, policy, _stateStore, discovery,
                    new ExecutableLauncher(target), _clock, _launchGate, _recorder),
                CancellationToken.None).ConfigureAwait(false);
            try
            {
                StoredConfiguration saved = await Task.Run(() =>
                    _configurationStore.Save(current, updated), CancellationToken.None)
                    .ConfigureAwait(false);
                _scheduler.Add(id, coordinator, policy);
                Configuration = saved;
                lock (_statusSync)
                {
                    _coordinators.Add(id, coordinator);
                    _statuses[id] = new(id, profile.Name, true, true, detected,
                        coordinator.Snapshot, null);
                }
                _scheduler.RequestImmediate(id);
                _recorder?.TryRecord(new(DateTimeOffset.UtcNow, Guid.NewGuid(),
                    EventSeverity.Information, OperationalEventKind.ManualAction,
                    ProfileId: id, ProfileName: profile.Name));
                return id;
            }
            catch
            {
                coordinator.Dispose();
                throw;
            }
        }
        finally { _changes.Release(); }
    }

    public IReadOnlyDictionary<Guid, Task> Pulse() => _scheduler.Pulse();

    public Task SetProfilePausedAsync(Guid profileId, bool paused,
        CancellationToken cancellationToken = default) =>
        RunProfileCommandAsync(profileId,
            coordinator => coordinator.SetPausedAsync(paused, cancellationToken));

    public Task ResetProfileRecoveryAsync(Guid profileId,
        CancellationToken cancellationToken = default) =>
        RunProfileCommandAsync(profileId,
            coordinator => coordinator.ResetRecoveryAsync(cancellationToken));

    public Task<CoordinatorResult> StartProfileNowAsync(Guid profileId,
        CancellationToken cancellationToken = default) =>
        RunProfileCommandAsync(profileId,
            coordinator => coordinator.StartNowAsync(cancellationToken));

    private Task RunProfileCommandAsync(Guid profileId,
        Func<ProfileCoordinator, Task> action)
    {
        Task command;
        lock (_statusSync)
        {
            if (_disposed) throw new ObjectDisposedException(nameof(RecoveryApplicationHost));
            if (!_coordinators.TryGetValue(profileId, out ProfileCoordinator? coordinator))
                throw new InvalidOperationException("This profile cannot accept recovery commands.");
            command = Task.Run(async () =>
            {
                await action(coordinator).ConfigureAwait(false);
                _scheduler.RequestImmediate(profileId);
            });
            _activeCommands.Add(command);
        }
        return ObserveCommandAsync(command);
    }

    private async Task ObserveCommandAsync(Task command)
    {
        try { await command.ConfigureAwait(false); }
        finally
        {
            lock (_statusSync) _activeCommands.Remove(command);
        }
    }

    private Task<TResult> RunProfileCommandAsync<TResult>(Guid profileId,
        Func<ProfileCoordinator, Task<TResult>> action)
    {
        Task<TResult> command;
        lock (_statusSync)
        {
            if (_disposed) throw new ObjectDisposedException(nameof(RecoveryApplicationHost));
            if (!_coordinators.TryGetValue(profileId, out ProfileCoordinator? coordinator))
                throw new InvalidOperationException("This profile cannot accept recovery commands.");
            command = Task.Run(async () =>
            {
                TResult result = await action(coordinator).ConfigureAwait(false);
                _scheduler.RequestImmediate(profileId);
                return result;
            });
            _activeCommands.Add(command);
        }
        return ObserveCommandAsync(command);
    }

    private async Task<TResult> ObserveCommandAsync<TResult>(Task<TResult> command)
    {
        try { return await command.ConfigureAwait(false); }
        finally
        {
            lock (_statusSync) _activeCommands.Remove(command);
        }
    }

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
        await _changes.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_disposed) return;
            lock (_statusSync) _disposed = true;
        }
        finally { _changes.Release(); }
        Task[] commands;
        lock (_statusSync) commands = _activeCommands.ToArray();
        try { await Task.WhenAll(commands).ConfigureAwait(false); }
        catch
        {
            // The command caller receives its failure. Cleanup still has to
            // release the scheduler and its process handles.
        }
        _recorder?.TryRecord(new(DateTimeOffset.UtcNow, Guid.NewGuid(),
            EventSeverity.Information, OperationalEventKind.Shutdown));
        await _scheduler.DisposeAsync().ConfigureAwait(false);
        if (_recorder is not null) await _recorder.DisposeAsync().ConfigureAwait(false);
        _journal?.Dispose();
        _launchGate.Dispose();
        _changes.Dispose();
    }
}

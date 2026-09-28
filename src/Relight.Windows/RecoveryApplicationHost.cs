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
    string? Problem,
    bool ConfiguredEnabled = true,
    RecoveryPolicy? Policy = null,
    TargetKind TargetKind = TargetKind.Executable,
    bool CanRepairRecoveryState = false,
    bool CanReplaceUnavailableProfile = false);

public sealed record ProfileBatchResult(int Requested, int Completed,
    IReadOnlyList<string> Errors);

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
    private readonly RecoverySessionStateStore? _sessionStateStore;
    private readonly SharedRecoveryBudgetStore? _sharedBudgetStore;
    private readonly bool _allowLegacyMigration;
    private readonly IRecoveryStateStore _activeStateStore;
    private OperationalEventJournal? _journal;
    private QueuedEventRecorder? _recorder;
    private LiveNotificationTap? _notificationTap;
    private readonly BoundedLaunchGate _launchGate = new();
    private readonly RecoveryScheduler _scheduler;
    private readonly IMonotonicClock _clock;
    private readonly Func<ExecutableTarget, IProcessLauncher> _executableLauncherFactory;
    private readonly Dictionary<Guid, HostedProfileStatus> _statuses = new();
    private readonly Dictionary<Guid, ProfileCoordinator> _coordinators = new();
    private readonly Dictionary<Task, Guid> _activeCommands = new();
    private readonly HashSet<Guid> _closingProfiles = [];
    private readonly object _statusSync = new();
    private readonly SemaphoreSlim _changes = new(1, 1);
    private volatile bool _sharedConfigurationSuspended;
    private bool _disposed;

    private RecoveryApplicationHost(string dataDirectory, IMonotonicClock clock,
        bool useSharedSessionState, bool allowLegacyMigration,
        Func<ExecutableTarget, IProcessLauncher>? executableLauncherFactory)
    {
        _configurationStore = new(dataDirectory);
        _stateStore = new(dataDirectory);
        _allowLegacyMigration = allowLegacyMigration;
        if (useSharedSessionState)
        {
            _sharedBudgetStore = new(dataDirectory);
            _sessionStateStore = new(dataDirectory,
                WindowsLogonSessionIdentity.Current().StorageKey, _sharedBudgetStore);
        }
        _activeStateStore = (IRecoveryStateStore?)_sessionStateStore ?? _stateStore;
        _clock = clock;
        _executableLauncherFactory = executableLauncherFactory ??
            (target => new ExecutableLauncher(target));
        _scheduler = new(clock);
    }

    public static string DefaultDataDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Relight");

    public StoredConfiguration? Configuration { get; private set; }
    public string? ConfigurationProblem { get; private set; }
    public TimeSpan Elapsed => _clock.Elapsed;

    public static async Task<RecoveryApplicationHost> OpenAsync(
        string? dataDirectory = null, IMonotonicClock? clock = null,
        CancellationToken cancellationToken = default)
        => await OpenCoreAsync(dataDirectory, clock, cancellationToken,
            useSharedSessionState: true, allowLegacyMigration: true)
            .ConfigureAwait(false);

    // Only upgrade tests open the former single-state host to create genuine
    // legacy snapshots. The ordinary application must use shared budgets.
    internal static Task<RecoveryApplicationHost> OpenLegacyForTestsAsync(
        string? dataDirectory = null, IMonotonicClock? clock = null,
        CancellationToken cancellationToken = default) =>
        OpenCoreAsync(dataDirectory, clock, cancellationToken,
            useSharedSessionState: false, allowLegacyMigration: false);

    internal static Task<RecoveryApplicationHost> OpenSharedSessionAsync(
        string? dataDirectory = null, IMonotonicClock? clock = null,
        bool allowLegacyMigration = false,
        CancellationToken cancellationToken = default,
        Func<ExecutableTarget, IProcessLauncher>? executableLauncherFactory = null) =>
        OpenCoreAsync(dataDirectory, clock, cancellationToken,
            useSharedSessionState: true, allowLegacyMigration,
            executableLauncherFactory);

    /// <summary>
    /// Opt-in WPF test entry point. Never migrates legacy state implicitly.
    /// Use an isolated data directory until cross-sign-in acceptance is complete.
    /// </summary>
    public static Task<RecoveryApplicationHost> OpenSharedSessionPreviewAsync(
        string dataDirectory, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(dataDirectory) || !Path.IsPathFullyQualified(dataDirectory))
            throw new ArgumentException("An absolute preview data directory is required.",
                nameof(dataDirectory));
        string fullPath = Path.GetFullPath(dataDirectory);
        if (string.Equals(fullPath.TrimEnd(Path.DirectorySeparatorChar),
            Path.GetFullPath(DefaultDataDirectory).TrimEnd(Path.DirectorySeparatorChar),
            StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException(
                "Shared-session preview requires a directory separate from normal Relight data.",
                nameof(dataDirectory));
        return OpenCoreAsync(fullPath, null, cancellationToken,
            useSharedSessionState: true, allowLegacyMigration: false);
    }

    private static async Task<RecoveryApplicationHost> OpenCoreAsync(
        string? dataDirectory, IMonotonicClock? clock,
        CancellationToken cancellationToken, bool useSharedSessionState,
        bool allowLegacyMigration,
        Func<ExecutableTarget, IProcessLauncher>? executableLauncherFactory = null)
    {
        var host = new RecoveryApplicationHost(dataDirectory ?? DefaultDataDirectory,
            clock ?? new StopwatchClock(), useSharedSessionState,
            allowLegacyMigration, executableLauncherFactory);
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

        await LoadProfilesAsync(Configuration, cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task LoadProfilesAsync(StoredConfiguration configuration,
        CancellationToken cancellationToken)
    {
        foreach (ProfileConfiguration profile in configuration.Configuration.Profiles)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!profile.Enabled)
            {
                RecoverySnapshot? disabledRecovery = null;
                string? problem = null;
                try
                {
                    disabledRecovery = RecoveryMachine.Restore(profile.Policy,
                        StateStoreForExisting(profile).Load(profile.Id).Checkpoint).Snapshot;
                }
                catch (Exception error) when (error is IOException or InvalidOperationException or ArgumentException)
                {
                    problem = $"Recovery state is unavailable; re-enable is blocked. {error.Message}";
                }
                bool canRepair = problem is not null && CanOfferCheckpointRepair(profile);
                bool canReplace = problem is not null && CanOfferProfileReplacement(profile);
                lock (_statusSync)
                    _statuses[profile.Id] = new(profile.Id, profile.Name, false, false,
                        null, disabledRecovery, problem, ConfiguredEnabled: false,
                        CanRepairRecoveryState: canRepair,
                        CanReplaceUnavailableProfile: canReplace);
                continue;
            }
            if (profile.Target.Kind == TargetKind.PackagedApplication &&
                string.Equals(profile.Target.Identity,
                    ChatGptPackagedDiscovery.ApplicationUserModelId,
                    StringComparison.OrdinalIgnoreCase))
            {
                var packagedDiscovery = new ChatGptPackagedDiscovery();
                if (!configuration.AutomaticActionsAllowed)
                {
                    HostedProfileStatus passive = await PassiveStatus(profile,
                        packagedDiscovery,
                        "Configuration is degraded; automatic actions are suspended.",
                        cancellationToken).ConfigureAwait(false);
                    lock (_statusSync) _statuses[profile.Id] = passive;
                    continue;
                }
                try
                {
                    ProfileCoordinator coordinator = ProfileCoordinator.OpenExisting(
                        profile.Id, profile.Policy, StateStoreForExisting(profile),
                        GuardDiscovery(packagedDiscovery),
                        GuardLauncher(new PackagedApplicationLauncher(profile.Target.Identity)),
                        _clock, _launchGate, _notificationTap,
                        stopper: GuardStopper(new ChatGptPackagedStopper()),
                        sharedBudget: _sharedBudgetStore);
                    _scheduler.Add(profile.Id, coordinator, profile.Policy);
                    lock (_statusSync)
                    {
                        _coordinators.Add(profile.Id, coordinator);
                        _statuses[profile.Id] = new(profile.Id, profile.Name, true, true,
                            null, coordinator.Snapshot, null);
                    }
                }
                catch (Exception error) when (error is IOException or
                    InvalidOperationException or ArgumentException)
                {
                    HostedProfileStatus passive = await PassiveStatus(profile,
                        packagedDiscovery,
                        $"Recovery state is unavailable; automatic actions are suspended. {error.Message}",
                        cancellationToken).ConfigureAwait(false);
                    bool canRepair = CanOfferCheckpointRepair(profile);
                    bool canReplace = CanOfferProfileReplacement(profile);
                    lock (_statusSync) _statuses[profile.Id] = passive with
                    {
                        CanRepairRecoveryState = canRepair,
                        CanReplaceUnavailableProfile = canReplace
                    };
                }
                continue;
            }
            if (profile.Target.Kind != TargetKind.Executable)
            {
                lock (_statusSync)
                    _statuses[profile.Id] = new(profile.Id, profile.Name, false, false,
                        null, null, "Installed-application activation is not available yet.");
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
                lock (_statusSync)
                    _statuses[profile.Id] = new(profile.Id, profile.Name, false, false,
                        null, null, $"Executable identity is invalid: {error.Message}");
                continue;
            }

            ExecutableDiscovery discovery;
            try { discovery = new(target); }
            catch (Exception error) when (error is InvalidOperationException or ArgumentException)
            {
                lock (_statusSync)
                    _statuses[profile.Id] = new(profile.Id, profile.Name, false, false,
                        null, null, $"Process discovery is unavailable: {error.Message}");
                continue;
            }
            if (!configuration.AutomaticActionsAllowed || !File.Exists(target.CanonicalPath))
            {
                string problem = !configuration.AutomaticActionsAllowed
                    ? "Configuration is degraded; automatic actions are suspended."
                    : "Target executable is missing; automatic actions are suspended.";
                HostedProfileStatus passive = await PassiveStatus(profile, discovery,
                    problem, cancellationToken).ConfigureAwait(false);
                lock (_statusSync) _statuses[profile.Id] = passive;
                continue;
            }

            try
            {
                ProfileCoordinator coordinator = ProfileCoordinator.OpenExisting(profile.Id,
                    profile.Policy, StateStoreForExisting(profile), GuardDiscovery(discovery),
                    GuardLauncher(_executableLauncherFactory(target)), _clock, _launchGate,
                    _notificationTap, GuardStopper(new ExecutableStopper(target)), _sharedBudgetStore);
                _scheduler.Add(profile.Id, coordinator, profile.Policy);
                lock (_statusSync)
                {
                    _coordinators.Add(profile.Id, coordinator);
                    _statuses[profile.Id] = new(profile.Id, profile.Name, true, true,
                        null, coordinator.Snapshot, null);
                }
            }
            catch (Exception error) when (error is IOException or InvalidOperationException or ArgumentException)
            {
                HostedProfileStatus passive = await PassiveStatus(profile, discovery,
                    $"Recovery state is unavailable; automatic actions are suspended. {error.Message}",
                    cancellationToken).ConfigureAwait(false);
                bool canRepair = CanOfferCheckpointRepair(profile);
                bool canReplace = CanOfferProfileReplacement(profile);
                lock (_statusSync) _statuses[profile.Id] = passive with
                {
                    CanRepairRecoveryState = canRepair,
                    CanReplaceUnavailableProfile = canReplace
                };
            }
        }
    }

    internal async Task<bool> ReconcileSharedConfigurationAsync(
        CancellationToken cancellationToken = default, bool forceReload = false)
    {
        if (_sessionStateStore is null)
            throw new InvalidOperationException("Shared-session reconciliation is not enabled.");
        await _changes.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_disposed) throw new ObjectDisposedException(nameof(RecoveryApplicationHost));
            StoredConfiguration current = _configurationStore.Load();
            if (!current.AutomaticActionsAllowed)
                throw new ConfigurationUnavailableException(
                    "Repair the invalid shared configuration before reconciliation.");
            StoredConfiguration? active = Configuration;
            if (!forceReload && !_sharedConfigurationSuspended && active is not null &&
                current.Revision == active.Revision &&
                string.Equals(current.ContentHash, active.ContentHash,
                    StringComparison.Ordinal))
            {
                ConfigurationProblem = null;
                return false;
            }
            cancellationToken.ThrowIfCancellationRequested();

            Guid[] previous;
            Task[] commands;
            lock (_statusSync)
            {
                previous = _statuses.Keys.ToArray();
                foreach (Guid id in previous) _closingProfiles.Add(id);
                commands = _activeCommands.Keys.ToArray();
            }
            try
            {
                try { await Task.WhenAll(commands).ConfigureAwait(false); }
                catch { /* Command callers observe their own failures. */ }
                foreach (Guid id in previous)
                    await _scheduler.RemoveAsync(id).ConfigureAwait(false);
                lock (_statusSync)
                {
                    _coordinators.Clear();
                    _statuses.Clear();
                }
                Configuration = current;
                // Once old coordinators are removed, finish rebuilding even if
                // the caller closes its dialog or cancels its request.
                await LoadProfilesAsync(current, CancellationToken.None)
                    .ConfigureAwait(false);
                _sharedConfigurationSuspended = false;
                ConfigurationProblem = current.Diagnostic;
                return true;
            }
            finally
            {
                lock (_statusSync)
                    foreach (Guid id in previous) _closingProfiles.Remove(id);
            }
        }
        catch (Exception error) when (error is IOException or InvalidOperationException or
            ArgumentException)
        {
            _sharedConfigurationSuspended = true;
            ConfigurationProblem =
                $"Shared configuration cannot be reconciled: {error.Message}";
            throw;
        }
        finally { _changes.Release(); }
    }

    private void InitializeLogging(string dataDirectory, GlobalConfiguration settings)
    {
        _journal = new(dataDirectory, settings);
        _recorder = new(_journal);
        _notificationTap = new(_recorder);
    }

    private IProcessDiscovery GuardDiscovery(IProcessDiscovery discovery) =>
        _sessionStateStore is null ? discovery :
            new ConfigurationGuardedDiscovery(discovery, _configurationStore,
                () => _sharedConfigurationSuspended ? null : Configuration);

    private IProcessLauncher GuardLauncher(IProcessLauncher launcher) =>
        _sessionStateStore is null ? launcher :
            new ConfigurationGuardedLauncher(launcher, _configurationStore,
                () => _sharedConfigurationSuspended ? null : Configuration);

    private IProcessStopper? GuardStopper(IProcessStopper? stopper) =>
        stopper is null || _sessionStateStore is null ? stopper :
            new ConfigurationGuardedStopper(stopper, _configurationStore,
                () => _sharedConfigurationSuspended ? null : Configuration);

    private void EnsureSharedConfigurationCurrent()
    {
        if (_sessionStateStore is null) return;
        if (ConfigurationGuardedDiscovery.CheckConfiguration(_configurationStore,
                _sharedConfigurationSuspended ? null : Configuration) is { } problem)
            throw new ConfigurationUnavailableException(problem);
    }

    private IRecoveryStateStore StateStoreForExisting(ProfileConfiguration profile)
    {
        if (_sessionStateStore is null) return _stateStore;
        SharedRecoveryBudgetStore budgets = _sharedBudgetStore!;
        bool sessionEvidence = _sessionStateStore.HasStateEvidence(profile.Id);
        bool budgetEvidence = budgets.HasBudgetEvidence(profile.Id);
        LegacyStateOwnership ownership = _stateStore.GetOwnership(profile.Id);
        bool legacyEvidence = _stateStore.HasStateEvidence(profile.Id);

        if (ownership == LegacyStateOwnership.MigrationPending)
        {
            if (!_allowLegacyMigration)
                throw new RecoveryStateUnavailableException(
                    "Recovery-state migration was interrupted; automatic actions remain suspended until repaired.");
            _stateStore.RepairPendingMigration(profile.Id, budgets, _sessionStateStore);
            return _sessionStateStore;
        }
        if (sessionEvidence)
        {
            if (!budgetEvidence)
                throw new RecoveryStateUnavailableException(
                    "Session state exists without its shared budget; automatic actions remain suspended.");
            if (legacyEvidence && ownership != LegacyStateOwnership.SessionOwner)
                throw new RecoveryStateUnavailableException(
                    "Shared and legacy recovery state conflict; automatic actions remain suspended.");
            _sessionStateStore.Load(profile.Id);
            return _sessionStateStore;
        }
        if (budgetEvidence)
        {
            if (legacyEvidence && ownership != LegacyStateOwnership.SessionOwner)
                throw new RecoveryStateUnavailableException(
                    "Shared and legacy recovery state conflict; automatic actions remain suspended.");
            _sessionStateStore.InitializeForNewSignIn(profile.Id, profile.Enabled);
            return _sessionStateStore;
        }
        if (!legacyEvidence || ownership == LegacyStateOwnership.SessionOwner)
            throw new RecoveryStateUnavailableException(
                "Recovery budget is missing for an existing profile; automatic actions remain suspended.");
        if (!_allowLegacyMigration)
            throw new RecoveryStateUnavailableException(
                "Legacy recovery state requires a guarded migration before shared-session monitoring can start.");
        _stateStore.MigrateToSession(profile.Id, budgets, _sessionStateStore);
        return _sessionStateStore;
    }

    private bool CanOfferCheckpointRepair(ProfileConfiguration profile)
    {
        if (_sessionStateStore is null || _sharedBudgetStore is null ||
            Configuration?.AutomaticActionsAllowed != true) return false;
        try
        {
            LegacyStateOwnership ownership = _stateStore.GetOwnership(profile.Id);
            if (ownership == LegacyStateOwnership.MigrationPending ||
                ownership != LegacyStateOwnership.SessionOwner &&
                _stateStore.HasStateEvidence(profile.Id)) return false;
            return _sessionStateStore.CanRepairUnavailableCheckpoint(profile.Id);
        }
        catch (Exception error) when (error is IOException or InvalidOperationException or
            ArgumentException)
        {
            return false;
        }
    }

    private bool CanOfferProfileReplacement(ProfileConfiguration profile)
    {
        if (_sessionStateStore is null || _sharedBudgetStore is null ||
            Configuration?.AutomaticActionsAllowed != true) return false;
        try
        {
            LegacyStateOwnership ownership = _stateStore.GetOwnership(profile.Id);
            if (ownership == LegacyStateOwnership.MigrationPending ||
                ownership != LegacyStateOwnership.SessionOwner &&
                _stateStore.HasStateEvidence(profile.Id)) return false;
            try { _sharedBudgetStore.Load(profile.Id); return false; }
            catch (RecoveryStateUnavailableException error)
            {
                return error.InnerException is null or System.Text.Json.JsonException;
            }
        }
        catch (Exception error) when (error is IOException or InvalidOperationException or
            ArgumentException)
        {
            return false;
        }
    }

    public async Task<Guid> ReplaceUnavailableProfileAsync(Guid profileId,
        CancellationToken cancellationToken = default)
    {
        if (_sessionStateStore is null || _sharedBudgetStore is null)
            throw new InvalidOperationException(
                "Unavailable-budget replacement requires shared-session mode.");
        Guid replacementId;
        await _changes.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_disposed) throw new ObjectDisposedException(nameof(RecoveryApplicationHost));
            EnsureSharedConfigurationCurrent();
            StoredConfiguration current = Configuration!;
            ProfileConfiguration profile = current.Configuration.Profiles
                .SingleOrDefault(item => item.Id == profileId) ??
                throw new InvalidOperationException("Profile no longer exists.");
            lock (_statusSync)
            {
                if (_coordinators.ContainsKey(profileId) ||
                    _closingProfiles.Contains(profileId))
                    throw new InvalidOperationException(
                        "This profile is active or already changing.");
            }
            if (!CanOfferProfileReplacement(profile))
                throw new RecoveryStateUnavailableException(
                    "This profile does not have a missing or untrusted shared budget.");
            replacementId = Guid.NewGuid();
            ProfileConfiguration replacement = profile with
            {
                Id = replacementId,
                Enabled = false
            };
            RelightConfiguration updated = current.Configuration with
            {
                Profiles = [.. current.Configuration.Profiles
                    .Select(item => item.Id == profileId ? replacement : item)]
            };
            ConfigurationStore.ValidateConfiguration(updated);
            await Task.Run(() =>
            {
                _sharedBudgetStore.Create(replacementId);
                _sessionStateStore.Create(replacementId,
                    new RecoveryMachine(replacement.Policy, enabled: false)
                        .ExportCheckpoint());
            }, cancellationToken).ConfigureAwait(false);
            StoredConfiguration saved = await Task.Run(() =>
                _configurationStore.Save(current, updated),
                CancellationToken.None).ConfigureAwait(false);
            Configuration = saved;
            Guid operationId = Guid.NewGuid();
            _recorder?.TryRecord(new(DateTimeOffset.UtcNow, Guid.NewGuid(),
                EventSeverity.Warning, OperationalEventKind.RecoveryProfileReplaced,
                ProfileId: profileId, ProfileName: profile.Name,
                OperationId: operationId));
            _recorder?.TryRecord(new(DateTimeOffset.UtcNow, Guid.NewGuid(),
                EventSeverity.Warning, OperationalEventKind.RecoveryProfileReplaced,
                ProfileId: replacementId, ProfileName: replacement.Name,
                OperationId: operationId));
        }
        finally { _changes.Release(); }
        await ReconcileSharedConfigurationAsync(CancellationToken.None,
            forceReload: true).ConfigureAwait(false);
        return replacementId;
    }

    public async Task RepairUnavailableRecoveryStateAsync(Guid profileId,
        CancellationToken cancellationToken = default)
    {
        if (_sessionStateStore is null)
            throw new InvalidOperationException(
                "Checkpoint repair is available only in shared-session mode.");
        await _changes.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_disposed) throw new ObjectDisposedException(nameof(RecoveryApplicationHost));
            EnsureSharedConfigurationCurrent();
            ProfileConfiguration profile = Configuration!.Configuration.Profiles
                .SingleOrDefault(item => item.Id == profileId) ??
                throw new InvalidOperationException("Profile no longer exists.");
            lock (_statusSync)
            {
                if (_coordinators.ContainsKey(profileId) ||
                    _closingProfiles.Contains(profileId))
                    throw new InvalidOperationException(
                        "This profile is active or already changing.");
            }
            if (!CanOfferCheckpointRepair(profile))
                throw new RecoveryStateUnavailableException(
                    "This profile has no repairable session checkpoint; automatic actions remain suspended.");
            await Task.Run(() => _sessionStateStore.RepairUnavailableCheckpointExplicitly(
                profileId, profile.Enabled), cancellationToken).ConfigureAwait(false);
            _recorder?.TryRecord(new(DateTimeOffset.UtcNow, Guid.NewGuid(),
                EventSeverity.Warning, OperationalEventKind.RecoveryStateRepaired,
                ProfileId: profileId, ProfileName: profile.Name));
        }
        finally { _changes.Release(); }
        // Once committed, rebuild even if the initiating UI command was cancelled.
        await ReconcileSharedConfigurationAsync(CancellationToken.None,
            forceReload: true).ConfigureAwait(false);
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
        IReadOnlyDictionary<Guid, ProfileConfiguration> configured =
            Configuration?.Configuration.Profiles.ToDictionary(profile => profile.Id) ??
            new Dictionary<Guid, ProfileConfiguration>();
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
                    Policy = configured.GetValueOrDefault(status.Id)?.Policy,
                    TargetKind = configured.GetValueOrDefault(status.Id)?.Target.Kind ??
                        status.TargetKind,
                    Detection = _scheduler.GetPassiveLast(status.Id) ?? status.Detection,
                    Recovery = coordinator?.Snapshot ?? status.Recovery,
                    AutomaticActionsAllowed = status.AutomaticActionsAllowed &&
                        !(coordinator?.StorageDegraded ?? false),
                    Problem = coordinator?.StorageError ?? status.Problem
                };
            return status with
            {
                Policy = configured.GetValueOrDefault(status.Id)?.Policy,
                TargetKind = configured.GetValueOrDefault(status.Id)?.Target.Kind ??
                    status.TargetKind,
                Recovery = coordinator?.Snapshot ?? last.Value.Result?.Snapshot ?? status.Recovery,
                AutomaticActionsAllowed = status.AutomaticActionsAllowed &&
                    !(coordinator?.StorageDegraded ?? last.Value.Result?.StorageDegraded ?? false),
                Problem = last.Value.Error ?? coordinator?.StorageError ??
                    last.Value.Result?.Error ?? status.Problem
            };
        }).ToArray();
    }

    public IReadOnlyList<OperationalEvent> DrainNotificationEvents() =>
        _notificationTap?.Drain() ?? [];

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
    public Task<Guid> RegisterExecutableAsync(string name, string executablePath,
        CancellationToken cancellationToken = default) =>
        RegisterExecutableAsync(name, executablePath, [], null, cancellationToken);

    public async Task<Guid> RegisterExecutableAsync(string name, string executablePath,
        IReadOnlyList<string> arguments, string? workingDirectory,
        CancellationToken cancellationToken = default) =>
        await RegisterExecutableAsync(name, executablePath, arguments,
            workingDirectory, RecoveryPolicy.Default, cancellationToken).ConfigureAwait(false);

    public async Task<Guid> RegisterExecutableAsync(string name, string executablePath,
        IReadOnlyList<string> arguments, string? workingDirectory,
        RecoveryPolicy policy, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(policy);
        policy.Validate();
        if (string.IsNullOrWhiteSpace(name) || name.Length > 100)
            throw new ArgumentException("Choose a name of 1–100 characters.", nameof(name));
        ArgumentNullException.ThrowIfNull(arguments);
        var target = new ExecutableTarget(executablePath, arguments.ToArray(),
            string.IsNullOrWhiteSpace(workingDirectory) ? null : workingDirectory.Trim());
        target.Validate();
        if (!File.Exists(target.CanonicalPath))
            throw new FileNotFoundException("The executable file does not exist.", target.CanonicalPath);
        if (target.WorkingDirectory is { } directory &&
            !Directory.Exists(Environment.ExpandEnvironmentVariables(directory)))
            throw new DirectoryNotFoundException("The working directory does not exist.");
        var discovery = new ExecutableDiscovery(target);
        Detection detected = await discovery.DetectAsync(cancellationToken).ConfigureAwait(false);
        if (detected.Kind == DetectionKind.Unavailable)
            throw new InvalidOperationException($"Target identity cannot be verified: {detected.Reason}");

        return await RegisterNewProfileAsync(name,
            new(TargetKind.Executable, target.CanonicalPath, target.Arguments.ToList(),
                target.WorkingDirectory), discovery,
            _executableLauncherFactory(target), new ExecutableStopper(target), detected,
            policy, cancellationToken).ConfigureAwait(false);
    }

    public static Task<Detection> InspectSelectedChatGptAsync(
        CancellationToken cancellationToken = default) =>
        new ChatGptPackagedDiscovery().DetectAsync(cancellationToken);

    public async Task<Guid> RegisterSelectedChatGptAsync(string name,
        CancellationToken cancellationToken = default) =>
        await RegisterSelectedChatGptAsync(name, RecoveryPolicy.Default,
            cancellationToken).ConfigureAwait(false);

    public async Task<Guid> RegisterSelectedChatGptAsync(string name,
        RecoveryPolicy policy, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(policy);
        policy.Validate();
        if (string.IsNullOrWhiteSpace(name) || name.Length > 100)
            throw new ArgumentException("Choose a name of 1–100 characters.", nameof(name));
        var discovery = new ChatGptPackagedDiscovery();
        Detection detected = await discovery.DetectAsync(cancellationToken)
            .ConfigureAwait(false);
        if (detected.Kind == DetectionKind.Unavailable)
            throw new InvalidOperationException(
                $"Target identity cannot be verified: {detected.Reason}");
        return await RegisterNewProfileAsync(name,
            new(TargetKind.PackagedApplication,
                ChatGptPackagedDiscovery.ApplicationUserModelId, []), discovery,
            new PackagedApplicationLauncher(
                ChatGptPackagedDiscovery.ApplicationUserModelId),
            new ChatGptPackagedStopper(), detected,
            policy, cancellationToken).ConfigureAwait(false);
    }

    private async Task<Guid> RegisterNewProfileAsync(string name,
        TargetConfiguration targetConfiguration, IProcessDiscovery discovery,
        IProcessLauncher launcher, IProcessStopper? stopper, Detection detected,
        RecoveryPolicy policy, CancellationToken cancellationToken)
    {
        await _changes.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_disposed) throw new ObjectDisposedException(nameof(RecoveryApplicationHost));
            StoredConfiguration current = Configuration ??
                throw new ConfigurationUnavailableException("Configuration is unavailable.");
            if (!current.AutomaticActionsAllowed)
                throw new ConfigurationUnavailableException("Repair configuration before adding a profile.");

            Guid id = Guid.NewGuid();
            var profile = new ProfileConfiguration(id, name.Trim(), true,
                targetConfiguration, policy);
            var updated = current.Configuration with
            {
                Profiles = [.. current.Configuration.Profiles, profile]
            };
            // Validate overlaps and the on-disk revision before reserving a new
            // profile ID. The final Save repeats both checks atomically.
            ConfigurationStore.ValidateConfiguration(updated);
            ProfileCoordinator coordinator = await Task.Run(() =>
                ProfileCoordinator.CreateNew(id, policy, _activeStateStore,
                    GuardDiscovery(discovery),
                    GuardLauncher(launcher), _clock, _launchGate, _notificationTap,
                    GuardStopper(stopper),
                    _sharedBudgetStore),
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

    public Task SetStartAtSignInAsync(bool enabled,
        CurrentUserStartupRegistration startup,
        CancellationToken cancellationToken = default) => Task.Run(async () =>
    {
        ArgumentNullException.ThrowIfNull(startup);
        await _changes.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_disposed) throw new ObjectDisposedException(nameof(RecoveryApplicationHost));
            StoredConfiguration current = Configuration ??
                throw new ConfigurationUnavailableException("Configuration is unavailable.");
            if (!current.AutomaticActionsAllowed)
                throw new ConfigurationUnavailableException(
                    "Repair configuration before changing sign-in startup.");
            var updated = current.Configuration with
            {
                Settings = current.Configuration.Settings with { StartAtSignIn = enabled }
            };
            ConfigurationStore.ValidateConfiguration(updated);
            cancellationToken.ThrowIfCancellationRequested();
            StartupRegistrationChange change = startup.Apply(enabled);
            try
            {
                // Check the shared configuration revision even when the saved
                // preference already matches and only the Run entry drifted.
                Configuration = _configurationStore.Save(current, updated);
            }
            catch (Exception saveError)
            {
                try { change.Rollback(); }
                catch (Exception rollbackError)
                {
                    throw new AggregateException(
                        "Configuration could not be saved and sign-in startup could not be rolled back.",
                        saveError, rollbackError);
                }
                throw;
            }
        }
        finally { _changes.Release(); }
    }, CancellationToken.None);

    public Task<string?> RepairConfigurationAsync(CancellationToken cancellationToken = default) =>
        Task.Run(async () =>
        {
            await _changes.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (_disposed) throw new ObjectDisposedException(nameof(RecoveryApplicationHost));
                StoredConfiguration fallback = Configuration is { FromLastGoodBackup: true } shown
                    ? shown : throw new InvalidOperationException(
                        "A trusted last-good configuration is not available for repair.");
                cancellationToken.ThrowIfCancellationRequested();
                return _configurationStore.RepairFromLastGood(fallback).PreservedInvalidPath;
            }
            finally { _changes.Release(); }
        }, CancellationToken.None);

    public ProfileConfiguration GetProfileForEdit(Guid profileId)
    {
        StoredConfiguration current = Configuration ??
            throw new ConfigurationUnavailableException("Configuration is unavailable.");
        ProfileConfiguration profile = current.Configuration.Profiles.SingleOrDefault(
            item => item.Id == profileId) ??
            throw new InvalidOperationException("Profile no longer exists.");
        return profile with
        {
            Target = profile.Target with { Arguments = [.. profile.Target.Arguments] }
        };
    }

    public Task UpdateProfileBasicsAsync(Guid profileId, string name,
        RecoveryPolicy policy, CancellationToken cancellationToken = default) =>
        Task.Run(() => UpdateProfileBasicsCoreAsync(profileId, name, policy,
            null, null, cancellationToken), CancellationToken.None);

    public Task UpdateProfileSettingsAsync(Guid profileId, string name,
        RecoveryPolicy policy, bool notifyOnRecovery, bool notifyOnLockout,
        CancellationToken cancellationToken = default) =>
        Task.Run(() => UpdateProfileBasicsCoreAsync(profileId, name, policy,
            notifyOnRecovery, notifyOnLockout, cancellationToken),
            CancellationToken.None);

    public Task UpdateProfileDefinitionAsync(Guid profileId, string name,
        TargetConfiguration target, RecoveryPolicy policy, bool notifyOnRecovery,
        bool notifyOnLockout, CancellationToken cancellationToken = default) =>
        Task.Run(() => UpdateProfileDefinitionCoreAsync(profileId, name, target,
            policy, notifyOnRecovery, notifyOnLockout, cancellationToken),
            CancellationToken.None);

    private async Task UpdateProfileDefinitionCoreAsync(Guid profileId, string name,
        TargetConfiguration target, RecoveryPolicy policy, bool notifyOnRecovery,
        bool notifyOnLockout, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(target);
        if (string.IsNullOrWhiteSpace(name) || name.Trim().Length > 100)
            throw new ArgumentException("Choose a name of 1–100 characters.", nameof(name));
        policy.Validate();
        if (TargetEquivalent(GetProfileForEdit(profileId).Target, target))
        {
            await UpdateProfileBasicsCoreAsync(profileId, name, policy,
                notifyOnRecovery, notifyOnLockout, cancellationToken).ConfigureAwait(false);
            return;
        }
        await _changes.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            StoredConfiguration current = Configuration ??
                throw new ConfigurationUnavailableException("Configuration is unavailable.");
            ProfileConfiguration profile = current.Configuration.Profiles.SingleOrDefault(
                item => item.Id == profileId) ??
                throw new InvalidOperationException("Profile no longer exists.");
            if (profile.Target.Kind != TargetKind.Executable ||
                target.Kind != TargetKind.Executable)
                throw new InvalidOperationException("Installed-app identities cannot be edited.");
            var executable = new ExecutableTarget(target.Identity, target.Arguments,
                target.WorkingDirectory, target.RequiredArgument, target.ExcludedArgument);
            executable.Validate();
            if (!File.Exists(executable.CanonicalPath))
                throw new FileNotFoundException("The executable file does not exist.",
                    executable.CanonicalPath);
            if (executable.WorkingDirectory is { } directory &&
                !Directory.Exists(Environment.ExpandEnvironmentVariables(directory)))
                throw new DirectoryNotFoundException("The working directory does not exist.");
            var normalized = target with { Identity = executable.CanonicalPath,
                Arguments = [.. target.Arguments], WorkingDirectory = executable.WorkingDirectory };
            bool identityChanged = !TargetEquivalent(profile.Target, normalized);
            if (!identityChanged)
                throw new InvalidOperationException("Target identity changed concurrently; reopen the editor.");
            Detection detected = await new ExecutableDiscovery(executable)
                .DetectAsync(cancellationToken).ConfigureAwait(false);
            if (detected.Kind == DetectionKind.Unavailable)
                throw new InvalidOperationException(
                    $"Target identity cannot be verified: {detected.Reason}");
            if (!current.AutomaticActionsAllowed)
                throw new ConfigurationUnavailableException(
                    "Repair configuration before editing profiles.");
            var edited = profile with { Name = name.Trim(), Target = normalized,
                Policy = policy, NotifyOnRecovery = notifyOnRecovery,
                NotifyOnLockout = notifyOnLockout };
            var updated = current.Configuration with { Profiles = current.Configuration.Profiles
                .Select(item => item.Id == profileId ? edited : item).ToList() };
            ConfigurationStore.ValidateConfiguration(updated);
            EnsureSharedConfigurationCurrent();
            ProfileCoordinator? coordinator;
            Task[] active;
            lock (_statusSync)
            {
                _closingProfiles.Add(profileId);
                _coordinators.TryGetValue(profileId, out coordinator);
                active = _activeCommands.Where(pair => pair.Value == profileId)
                    .Select(pair => pair.Key).ToArray();
            }
            try
            {
                try { await Task.WhenAll(active).ConfigureAwait(false); }
                catch { /* Command callers receive their own failures. */ }
                StoredConfiguration? saved = null;
                if (coordinator is not null)
                    await coordinator.RetireForIdentityChangeAsync(
                        () => saved = _configurationStore.Save(current, updated),
                        cancellationToken).ConfigureAwait(false);
                else
                    saved = _configurationStore.Save(current, updated);
                Configuration = saved;
                await _scheduler.RemoveAsync(profileId).ConfigureAwait(false);
                lock (_statusSync)
                {
                    _coordinators.Remove(profileId);
                    _statuses.Remove(profileId);
                }
                await LoadProfilesAsync(saved! with { Configuration = saved.Configuration with
                    { Profiles = [edited] } }, CancellationToken.None).ConfigureAwait(false);
                _scheduler.RequestImmediate(profileId);
                _recorder?.TryRecord(new(DateTimeOffset.UtcNow, Guid.NewGuid(),
                    EventSeverity.Information, OperationalEventKind.ProfileTargetChanged,
                    ProfileId: profileId, ProfileName: edited.Name,
                    EpisodeId: coordinator?.Snapshot.EpisodeId));
            }
            finally
            {
                lock (_statusSync) _closingProfiles.Remove(profileId);
            }
        }
        finally { _changes.Release(); }
    }

    private static bool TargetEquivalent(TargetConfiguration left, TargetConfiguration right) =>
        left.Kind == right.Kind &&
        string.Equals(left.Identity, right.Identity, StringComparison.OrdinalIgnoreCase) &&
        left.Arguments.SequenceEqual(right.Arguments, StringComparer.Ordinal) &&
        string.Equals(left.WorkingDirectory, right.WorkingDirectory,
            StringComparison.OrdinalIgnoreCase) &&
        string.Equals(left.RequiredArgument, right.RequiredArgument, StringComparison.Ordinal) &&
        string.Equals(left.ExcludedArgument, right.ExcludedArgument, StringComparison.Ordinal);

    private async Task UpdateProfileBasicsCoreAsync(Guid profileId, string name,
        RecoveryPolicy policy, bool? notifyOnRecovery, bool? notifyOnLockout,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Trim().Length > 100)
            throw new ArgumentException("Choose a name of 1–100 characters.", nameof(name));
        ArgumentNullException.ThrowIfNull(policy);
        policy.Validate();
        await _changes.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_disposed) throw new ObjectDisposedException(nameof(RecoveryApplicationHost));
            StoredConfiguration current = Configuration ??
                throw new ConfigurationUnavailableException("Configuration is unavailable.");
            if (!current.AutomaticActionsAllowed)
                throw new ConfigurationUnavailableException(
                    "Repair configuration before editing profiles.");
            ProfileConfiguration profile = current.Configuration.Profiles.SingleOrDefault(
                item => item.Id == profileId) ??
                throw new InvalidOperationException("Profile no longer exists.");
            string nextName = name.Trim();
            bool renamed = !string.Equals(profile.Name, nextName, StringComparison.Ordinal);
            bool policyChanged = profile.Policy != policy;
            bool preferencesChanged =
                notifyOnRecovery is { } recovery && recovery != profile.NotifyOnRecovery ||
                notifyOnLockout is { } lockout && lockout != profile.NotifyOnLockout;
            if (!renamed && !policyChanged && !preferencesChanged) return;

            var edited = profile with
            {
                Name = nextName,
                Policy = policy,
                NotifyOnRecovery = notifyOnRecovery ?? profile.NotifyOnRecovery,
                NotifyOnLockout = notifyOnLockout ?? profile.NotifyOnLockout
            };
            var updated = current.Configuration with
            {
                Profiles = current.Configuration.Profiles.Select(item => item.Id == profileId
                    ? edited : item).ToList()
            };
            ConfigurationStore.ValidateConfiguration(updated);
            ProfileCoordinator? coordinator;
            lock (_statusSync) _coordinators.TryGetValue(profileId, out coordinator);
            StoredConfiguration saved;
            if (policyChanged && coordinator is not null)
            {
                StoredConfiguration? committed = null;
                await coordinator.ApplyPolicyChangeAsync(policy,
                    () => committed = _configurationStore.Save(current, updated),
                    cancellationToken).ConfigureAwait(false);
                saved = committed!;
                Configuration = saved;
                _scheduler.UpdatePolicy(profileId, policy);
            }
            else
            {
                cancellationToken.ThrowIfCancellationRequested();
                saved = _configurationStore.Save(current, updated);
                Configuration = saved;
                if (policyChanged && profile.Enabled)
                    _scheduler.UpdatePassiveInterval(profileId,
                        policy.LockoutDiscoveryInterval);
            }
            lock (_statusSync)
            {
                if (_statuses.TryGetValue(profileId, out HostedProfileStatus? status))
                    _statuses[profileId] = status with { Name = nextName,
                        Recovery = coordinator?.Snapshot ?? status.Recovery };
            }
            if (renamed)
                _recorder?.TryRecord(new(DateTimeOffset.UtcNow, Guid.NewGuid(),
                    EventSeverity.Information, OperationalEventKind.ProfileRenamed,
                    ProfileId: profileId, ProfileName: nextName,
                    EpisodeId: coordinator?.Snapshot.EpisodeId));
            if (policyChanged && coordinator is null)
                _recorder?.TryRecord(new(DateTimeOffset.UtcNow, Guid.NewGuid(),
                    EventSeverity.Information, OperationalEventKind.PolicyChanged,
                    ProfileId: profileId, ProfileName: nextName));
            if (preferencesChanged)
                _recorder?.TryRecord(new(DateTimeOffset.UtcNow, Guid.NewGuid(),
                    EventSeverity.Information, OperationalEventKind.NotificationPreferencesChanged,
                    ProfileId: profileId, ProfileName: nextName));
        }
        finally { _changes.Release(); }
    }

    public Task SetProfilePausedAsync(Guid profileId, bool paused,
        CancellationToken cancellationToken = default) =>
        RunProfileCommandAsync(profileId,
            coordinator => coordinator.SetPausedAsync(paused, cancellationToken));

    public async Task<ProfileBatchResult> SetAllPausedAsync(bool paused,
        CancellationToken cancellationToken = default)
    {
        (Guid Id, string Name)[] targets = GetProfiles()
            .Where(profile => profile.Recovery is { Enabled: true } recovery &&
                recovery.Paused != paused && profile.AutomaticActionsAllowed &&
                profile.Problem is null)
            .Select(profile => (profile.Id, profile.Name))
            .ToArray();
        Task<(bool Succeeded, string? Error)>[] commands = targets.Select(async target =>
        {
            try
            {
                await SetProfilePausedAsync(target.Id, paused, cancellationToken)
                    .ConfigureAwait(false);
                return (true, (string?)null);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception error)
            {
                return (false, $"{target.Name}: {error.Message}");
            }
        }).ToArray();
        (bool Succeeded, string? Error)[] outcomes = await Task.WhenAll(commands)
            .ConfigureAwait(false);
        return new(targets.Length, outcomes.Count(outcome => outcome.Succeeded),
            outcomes.Where(outcome => outcome.Error is not null)
                .Select(outcome => outcome.Error!).ToArray());
    }

    public Task ResetProfileRecoveryAsync(Guid profileId,
        CancellationToken cancellationToken = default) =>
        RunProfileCommandAsync(profileId,
            coordinator => coordinator.ResetRecoveryAsync(cancellationToken));

    public Task<CoordinatorResult> StartProfileNowAsync(Guid profileId,
        CancellationToken cancellationToken = default) =>
        RunProfileCommandAsync(profileId,
            coordinator => coordinator.StartNowAsync(cancellationToken));

    public Task<StopCommandResult> StopProfileAndPauseAsync(Guid profileId,
        TimeSpan gracefulTimeout, CancellationToken cancellationToken = default) =>
        RunProfileCommandAsync(profileId,
            coordinator => coordinator.StopAndPauseAsync(gracefulTimeout, cancellationToken));

    public Task<StopCommandResult> StopProfileForRestartAsync(Guid profileId,
        TimeSpan gracefulTimeout, CancellationToken cancellationToken = default) =>
        RunProfileCommandAsync(profileId,
            coordinator => coordinator.StopForRestartAsync(gracefulTimeout, cancellationToken));

    public Task<TargetStopResult> ForceClosePausedProfileAsync(Guid profileId,
        Guid operationId, string selectedIdentity,
        CancellationToken cancellationToken = default) =>
        RunProfileCommandAsync(profileId,
            coordinator => coordinator.ForceClosePausedAsync(operationId,
                selectedIdentity, cancellationToken));

    public Task<CoordinatorResult> CompleteProfileRestartAsync(Guid profileId,
        Guid operationId, string? selectedIdentity,
        CancellationToken cancellationToken = default) =>
        RunProfileCommandAsync(profileId,
            coordinator => coordinator.CompleteRestartAsync(operationId,
                selectedIdentity, cancellationToken));

    public Task SetProfileEnabledAsync(Guid profileId, bool enabled,
        CancellationToken cancellationToken = default) =>
        Task.Run(() => ChangeProfileAsync(profileId, enabled, remove: false,
            cancellationToken), CancellationToken.None);

    public Task RemoveProfileAsync(Guid profileId,
        CancellationToken cancellationToken = default) =>
        Task.Run(() => ChangeProfileAsync(profileId, enabled: false, remove: true,
            cancellationToken), CancellationToken.None);

    public async Task<Guid> DuplicateProfileAsync(Guid profileId,
        CancellationToken cancellationToken = default)
    {
        await _changes.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_disposed) throw new ObjectDisposedException(nameof(RecoveryApplicationHost));
            StoredConfiguration current = Configuration ??
                throw new ConfigurationUnavailableException("Configuration is unavailable.");
            if (!current.AutomaticActionsAllowed)
                throw new ConfigurationUnavailableException(
                    "Repair configuration before duplicating a profile.");
            ProfileConfiguration source = current.Configuration.Profiles.SingleOrDefault(
                item => item.Id == profileId) ??
                throw new InvalidOperationException("Profile no longer exists.");
            Guid duplicateId = Guid.NewGuid();
            string duplicateName = $"Copy of {source.Name}";
            if (duplicateName.Length > 100) duplicateName = duplicateName[..100].TrimEnd();
            ProfileConfiguration duplicate = source with
            {
                Id = duplicateId,
                Name = duplicateName,
                Enabled = false
            };
            var updated = current.Configuration with
            {
                Profiles = [.. current.Configuration.Profiles, duplicate]
            };
            ConfigurationStore.ValidateConfiguration(updated);
            // Never copy the source budget or live process state. A failed save
            // leaves an unreferenced state file, not an enabled duplicate.
            await Task.Run(() =>
            {
                _sharedBudgetStore?.Create(duplicateId);
                _activeStateStore.Create(duplicateId,
                    new RecoveryMachine(duplicate.Policy, enabled: false).ExportCheckpoint());
            },
                CancellationToken.None).ConfigureAwait(false);
            StoredConfiguration saved = await Task.Run(() =>
                _configurationStore.Save(current, updated), CancellationToken.None)
                .ConfigureAwait(false);
            Configuration = saved;
            lock (_statusSync)
                _statuses[duplicateId] = new(duplicateId, duplicate.Name, false, false,
                    null, new RecoveryMachine(duplicate.Policy, enabled: false).Snapshot,
                    null, ConfiguredEnabled: false, Policy: duplicate.Policy,
                    TargetKind: duplicate.Target.Kind);
            _recorder?.TryRecord(new(DateTimeOffset.UtcNow, Guid.NewGuid(),
                EventSeverity.Information, OperationalEventKind.ManualAction,
                ProfileId: duplicateId, ProfileName: duplicate.Name));
            return duplicateId;
        }
        finally { _changes.Release(); }
    }

    private async Task ChangeProfileAsync(Guid profileId, bool enabled, bool remove,
        CancellationToken cancellationToken)
    {
        await _changes.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_disposed) throw new ObjectDisposedException(nameof(RecoveryApplicationHost));
            StoredConfiguration current = Configuration ??
                throw new ConfigurationUnavailableException("Configuration is unavailable.");
            if (!current.AutomaticActionsAllowed)
                throw new ConfigurationUnavailableException(
                    "Repair configuration before changing profiles.");
            ProfileConfiguration profile = current.Configuration.Profiles.SingleOrDefault(
                item => item.Id == profileId) ??
                throw new InvalidOperationException("Profile no longer exists.");
            if (!remove && profile.Enabled == enabled) return;

            var updatedProfiles = remove
                ? current.Configuration.Profiles.Where(item => item.Id != profileId).ToList()
                : current.Configuration.Profiles.Select(item => item.Id == profileId
                    ? item with { Enabled = enabled } : item).ToList();
            var updated = current.Configuration with { Profiles = updatedProfiles };
            ConfigurationStore.ValidateConfiguration(updated);

            if (enabled)
                await EnableProfileAsync(current, updated, profile, cancellationToken)
                    .ConfigureAwait(false);
            else
                await DisableOrRemoveProfileAsync(current, updated, profile, remove,
                    cancellationToken).ConfigureAwait(false);
        }
        finally { _changes.Release(); }
    }

    private async Task EnableProfileAsync(StoredConfiguration current,
        RelightConfiguration updated, ProfileConfiguration profile,
        CancellationToken cancellationToken)
    {
        IProcessDiscovery discovery;
        IProcessLauncher launcher;
        IProcessStopper? stopper = null;
        if (profile.Target.Kind == TargetKind.Executable)
        {
            var target = new ExecutableTarget(profile.Target.Identity,
                profile.Target.Arguments, profile.Target.WorkingDirectory,
                profile.Target.RequiredArgument, profile.Target.ExcludedArgument);
            target.Validate();
            if (!File.Exists(target.CanonicalPath))
                throw new FileNotFoundException("The target executable is missing.",
                    target.CanonicalPath);
            discovery = new ExecutableDiscovery(target);
            launcher = _executableLauncherFactory(target);
            stopper = new ExecutableStopper(target);
        }
        else if (profile.Target.Kind == TargetKind.PackagedApplication &&
            string.Equals(profile.Target.Identity,
                ChatGptPackagedDiscovery.ApplicationUserModelId,
                StringComparison.OrdinalIgnoreCase))
        {
            discovery = new ChatGptPackagedDiscovery();
            launcher = new PackagedApplicationLauncher(profile.Target.Identity);
            stopper = new ChatGptPackagedStopper();
        }
        else throw new InvalidOperationException(
            "This packaged application does not have a validated adapter.");
        Detection found = await discovery.DetectAsync(cancellationToken).ConfigureAwait(false);
        if (found.Kind == DetectionKind.Unavailable)
            throw new InvalidOperationException(
                $"Target identity cannot be verified: {found.Reason}");
        lock (_statusSync)
            if (_coordinators.ContainsKey(profile.Id))
                throw new InvalidOperationException("This profile is already scheduled.");
        ProfileCoordinator coordinator = ProfileCoordinator.OpenExisting(profile.Id,
            profile.Policy, StateStoreForExisting(profile), GuardDiscovery(discovery), GuardLauncher(launcher),
            _clock, _launchGate, _notificationTap, GuardStopper(stopper), _sharedBudgetStore);
        RecoveryState previousState = coordinator.Snapshot.State;
        bool schedulerOwnsCoordinator = false;
        try
        {
            await coordinator.SetEnabledAsync(true, cancellationToken).ConfigureAwait(false);
            try
            {
                StoredConfiguration saved = _configurationStore.Save(current, updated);
                Configuration = saved;
            }
            catch
            {
                await coordinator.SetEnabledAsync(false, CancellationToken.None)
                    .ConfigureAwait(false);
                throw;
            }
            try
            {
                _scheduler.Add(profile.Id, coordinator, profile.Policy);
                schedulerOwnsCoordinator = true;
                lock (_statusSync)
                {
                    _coordinators.Add(profile.Id, coordinator);
                    _statuses[profile.Id] = new(profile.Id, profile.Name, true, true,
                        found, coordinator.Snapshot, null);
                }
                _scheduler.RequestImmediate(profile.Id);
                _recorder?.TryRecord(new(DateTimeOffset.UtcNow, Guid.NewGuid(),
                    EventSeverity.Information, OperationalEventKind.ProtectionEnabled,
                    ProfileId: profile.Id, ProfileName: profile.Name,
                    EpisodeId: coordinator.Snapshot.EpisodeId,
                    PreviousState: previousState, NewState: coordinator.Snapshot.State));
            }
            catch (Exception error)
            {
                if (schedulerOwnsCoordinator)
                {
                    await _scheduler.RemoveAsync(profile.Id).ConfigureAwait(false);
                }
                lock (_statusSync)
                {
                    _coordinators.Remove(profile.Id);
                    _statuses[profile.Id] = new(profile.Id, profile.Name, false, false,
                        found, coordinator.Snapshot,
                        $"Monitoring could not start: {error.Message}");
                }
                throw;
            }
        }
        finally
        {
            if (!schedulerOwnsCoordinator) coordinator.Dispose();
        }
    }

    private async Task DisableOrRemoveProfileAsync(StoredConfiguration current,
        RelightConfiguration updated, ProfileConfiguration profile, bool remove,
        CancellationToken cancellationToken)
    {
        ProfileCoordinator? coordinator;
        lock (_statusSync)
        {
            _closingProfiles.Add(profile.Id);
            _coordinators.TryGetValue(profile.Id, out coordinator);
        }
        bool stateChanged = false;
        RecoveryState? previousState = coordinator?.Snapshot.State;
        try
        {
            if (coordinator is not null)
            {
                await coordinator.SetEnabledAsync(false, cancellationToken)
                    .ConfigureAwait(false);
                stateChanged = true;
            }
            Task[] active;
            lock (_statusSync)
                active = _activeCommands.Where(pair => pair.Value == profile.Id)
                    .Select(pair => pair.Key).ToArray();
            try { await Task.WhenAll(active).ConfigureAwait(false); }
            catch { /* Each command caller receives its own failure. */ }

            cancellationToken.ThrowIfCancellationRequested();
            StoredConfiguration saved = _configurationStore.Save(current, updated);
            // After the durable configuration commit, cancellation cannot leave
            // the old scheduler or profile visible as though it were enabled.
            Configuration = saved;
            lock (_statusSync)
            {
                if (remove) _statuses.Remove(profile.Id);
                else _statuses[profile.Id] = new(profile.Id, profile.Name,
                    false, false, null, coordinator?.Snapshot, null,
                    ConfiguredEnabled: false);
            }
            await _scheduler.RemoveAsync(profile.Id).ConfigureAwait(false);
            lock (_statusSync)
            {
                _coordinators.Remove(profile.Id);
            }
            _recorder?.TryRecord(new(DateTimeOffset.UtcNow, Guid.NewGuid(),
                EventSeverity.Information, remove ? OperationalEventKind.ProfileRemoved :
                    OperationalEventKind.ProtectionDisabled,
                ProfileId: profile.Id, ProfileName: profile.Name,
                EpisodeId: coordinator?.Snapshot.EpisodeId,
                PreviousState: previousState, NewState: coordinator?.Snapshot.State));
        }
        catch
        {
            if (stateChanged && coordinator is not null &&
                Configuration?.Revision == current.Revision)
                await coordinator.SetEnabledAsync(true, CancellationToken.None)
                    .ConfigureAwait(false);
            throw;
        }
        finally
        {
            lock (_statusSync) _closingProfiles.Remove(profile.Id);
        }
    }

    private Task RunProfileCommandAsync(Guid profileId,
        Func<ProfileCoordinator, Task> action)
    {
        Task command;
        lock (_statusSync)
        {
            if (_disposed) throw new ObjectDisposedException(nameof(RecoveryApplicationHost));
            if (_closingProfiles.Contains(profileId))
                throw new InvalidOperationException("This profile is being changed.");
            if (!_coordinators.TryGetValue(profileId, out ProfileCoordinator? coordinator))
                throw new InvalidOperationException("This profile cannot accept recovery commands.");
            command = Task.Run(async () =>
            {
                EnsureSharedConfigurationCurrent();
                lock (_statusSync)
                    if (_closingProfiles.Contains(profileId))
                        throw new InvalidOperationException("This profile is being changed.");
                await action(coordinator).ConfigureAwait(false);
                _scheduler.RequestImmediate(profileId);
            });
            _activeCommands.Add(command, profileId);
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
            if (_closingProfiles.Contains(profileId))
                throw new InvalidOperationException("This profile is being changed.");
            if (!_coordinators.TryGetValue(profileId, out ProfileCoordinator? coordinator))
                throw new InvalidOperationException("This profile cannot accept recovery commands.");
            command = Task.Run(async () =>
            {
                EnsureSharedConfigurationCurrent();
                lock (_statusSync)
                    if (_closingProfiles.Contains(profileId))
                        throw new InvalidOperationException("This profile is being changed.");
                TResult result = await action(coordinator).ConfigureAwait(false);
                _scheduler.RequestImmediate(profileId);
                return result;
            });
            _activeCommands.Add(command, profileId);
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
        _sessionStateStore is null ? _scheduler.RunAsync(cancellationToken) :
            RunSharedSessionAsync(cancellationToken);

    private async Task RunSharedSessionAsync(CancellationToken cancellationToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        Task scheduler = _scheduler.RunAsync(linked.Token);
        Task watcher = WatchSharedConfigurationAsync(linked.Token);
        await Task.WhenAny(scheduler, watcher).ConfigureAwait(false);
        linked.Cancel();
        await Task.WhenAll(scheduler, watcher).ConfigureAwait(false);
    }

    private async Task WatchSharedConfigurationAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(2));
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken)
                       .ConfigureAwait(false))
            {
                try
                {
                    await ReconcileSharedConfigurationAsync(cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception error) when (error is IOException or InvalidOperationException or
                    ArgumentException)
                {
                    ConfigurationProblem =
                        $"Shared configuration cannot be reconciled: {error.Message}";
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
    }

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
        lock (_statusSync) commands = _activeCommands.Keys.ToArray();
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

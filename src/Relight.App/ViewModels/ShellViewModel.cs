using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Windows.Input;
using Relight.Storage;
using Relight.Windows;

namespace Relight.ViewModels;

internal enum ShellPage { Applications, History, Settings }
internal enum ApplicationStatusCategory { Attention, Recovering, Protected, PausedOrDisabled }
internal enum ApplicationSortMode { Name, Status, Attempts }
internal sealed record ApplicationFilterOption(string Label, ApplicationStatusCategory? Category);
internal sealed record ApplicationSortOption(string Label, ApplicationSortMode Mode);

internal sealed class ApplicationStatusRow(
    Guid id, string name, string state, string detail,
    bool isPaused, bool canPauseResume, bool canReset, bool canStartNow,
    bool canStopAndPause, bool canRestartNow,
    bool configuredEnabled, bool canToggleEnabled, bool canRemove, bool canEdit,
    ApplicationStatusCategory category, int reservedAttempts,
    ProfileTimingPresentation timing, string lastOutage,
    string lastAutomaticRecovery) : INotifyPropertyChanged
{
    public Guid Id { get; } = id;
    public string Name { get; private set; } = name;
    public string State { get; private set; } = state;
    public string Detail { get; private set; } = detail;
    public bool IsPaused { get; private set; } = isPaused;
    public bool CanPauseResume { get; private set; } = canPauseResume;
    public bool CanReset { get; private set; } = canReset;
    public bool CanStartNow { get; private set; } = canStartNow;
    public bool CanStopAndPause { get; private set; } = canStopAndPause;
    public bool CanRestartNow { get; private set; } = canRestartNow;
    public bool ConfiguredEnabled { get; private set; } = configuredEnabled;
    public bool CanToggleEnabled { get; private set; } = canToggleEnabled;
    public bool CanRemove { get; private set; } = canRemove;
    public bool CanEdit { get; private set; } = canEdit;
    public ApplicationStatusCategory Category { get; private set; } = category;
    public int ReservedAttempts { get; private set; } = reservedAttempts;
    public string NextAction { get; private set; } = timing.NextAction;
    public string LastSeen { get; private set; } = timing.LastSeen;
    public string Attempts { get; private set; } = timing.Attempts;
    public string Instance { get; private set; } = timing.Instance;
    public string LastOutage { get; private set; } = lastOutage;
    public string LastAutomaticRecovery { get; private set; } = lastAutomaticRecovery;
    public string PauseResumeLabel => IsPaused ? "Resume protection" : "Pause protection";
    public string EnableDisableLabel => ConfiguredEnabled ? "Disable protection" :
        "Enable protection";

    public bool UpdateFrom(ApplicationStatusRow next)
    {
        if (Id != next.Id) throw new ArgumentException("Profile IDs do not match.", nameof(next));
        bool changed = Name != next.Name || State != next.State || Detail != next.Detail ||
            IsPaused != next.IsPaused || CanPauseResume != next.CanPauseResume ||
            CanReset != next.CanReset || CanStartNow != next.CanStartNow ||
            CanStopAndPause != next.CanStopAndPause ||
            CanRestartNow != next.CanRestartNow ||
            ConfiguredEnabled != next.ConfiguredEnabled ||
            CanToggleEnabled != next.CanToggleEnabled || CanRemove != next.CanRemove ||
            CanEdit != next.CanEdit || Category != next.Category ||
            ReservedAttempts != next.ReservedAttempts || NextAction != next.NextAction ||
            LastSeen != next.LastSeen || Attempts != next.Attempts ||
            Instance != next.Instance || LastOutage != next.LastOutage ||
            LastAutomaticRecovery != next.LastAutomaticRecovery;
        if (!changed) return false;
        Name = next.Name;
        State = next.State;
        Detail = next.Detail;
        IsPaused = next.IsPaused;
        CanPauseResume = next.CanPauseResume;
        CanReset = next.CanReset;
        CanStartNow = next.CanStartNow;
        CanStopAndPause = next.CanStopAndPause;
        CanRestartNow = next.CanRestartNow;
        ConfiguredEnabled = next.ConfiguredEnabled;
        CanToggleEnabled = next.CanToggleEnabled;
        CanRemove = next.CanRemove;
        CanEdit = next.CanEdit;
        Category = next.Category;
        ReservedAttempts = next.ReservedAttempts;
        NextAction = next.NextAction;
        LastSeen = next.LastSeen;
        Attempts = next.Attempts;
        Instance = next.Instance;
        LastOutage = next.LastOutage;
        LastAutomaticRecovery = next.LastAutomaticRecovery;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(null));
        return true;
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}

internal sealed record HistoryProfileOption(string Label, Guid? Id);
internal sealed record HistorySeverityOption(string Label, EventSeverity? Minimum);
internal sealed record HistoryKindOption(string Label, OperationalEventKind? Kind);
internal sealed record HistoryRangeOption(string Label, TimeSpan? Lookback);
internal sealed record HistoryRow(string LocalTime, string UtcTime, string Profile,
    string Severity, string Kind, string Summary, string Details);

internal sealed class ShellViewModel : INotifyPropertyChanged
{
    private ShellPage _page;
    private IReadOnlyList<ApplicationStatusRow> _applicationRows = [];
    private IReadOnlyList<ApplicationStatusRow> _visibleApplicationRows = [];
    private ApplicationFilterOption _selectedApplicationFilter;
    private ApplicationSortOption _selectedApplicationSort;
    private string _monitoringBanner = "Loading monitoring configuration…";
    private string _footerStatus = "Relight is in the tray · Loading monitoring status";
    private string _trayStatus = "Relight · Loading monitoring status";
    private TrayIconState _trayIconState = TrayIconState.Paused;
    private bool _canRepairConfiguration;
    private bool _startAtSignIn;
    private bool _canChangeStartAtSignIn;
    private string _startAtSignInStatus = "Checking current-user startup registration…";
    private IReadOnlyList<HistoryProfileOption> _historyProfiles =
        [new("All applications", null)];
    private IReadOnlyList<EventHistoryProfile> _retainedHistoryProfiles = [];
    private IReadOnlyList<HistoryRow> _historyRows = [];
    private string _historyStatus = "Open History to load recorded events.";
    private string _historySummaryText = "Open History to see an event-based summary.";
    private HistoryProfileOption _selectedHistoryProfile;
    private HistorySeverityOption _selectedHistorySeverity;
    private HistoryKindOption _selectedHistoryKind;
    private HistoryRangeOption _selectedHistoryRange;
    private string _historyEpisodeText = "";

    public ShellViewModel(Action hide, Action exit, Action add, Action repairConfiguration)
    {
        ApplicationsCommand = new RelayCommand(() => Navigate(ShellPage.Applications));
        HistoryCommand = new RelayCommand(() => Navigate(ShellPage.History));
        SettingsCommand = new RelayCommand(() => Navigate(ShellPage.Settings));
        HideCommand = new RelayCommand(hide);
        ExitCommand = new RelayCommand(exit);
        AddCommand = new RelayCommand(add);
        RepairConfigurationCommand = new RelayCommand(repairConfiguration);
        RefreshHistoryCommand = new RelayCommand(() => HistoryRefreshRequested?.Invoke(this, EventArgs.Empty));
        _selectedHistoryProfile = _historyProfiles[0];
        _selectedApplicationFilter = ApplicationFilters[0];
        _selectedApplicationSort = ApplicationSorts[0];
        _selectedHistorySeverity = HistorySeverities[0];
        _selectedHistoryKind = HistoryKinds[0];
        _selectedHistoryRange = HistoryRanges[0];
    }

    public ICommand ApplicationsCommand { get; }
    public ICommand HistoryCommand { get; }
    public ICommand SettingsCommand { get; }
    public ICommand HideCommand { get; }
    public ICommand ExitCommand { get; }
    public ICommand AddCommand { get; }
    public ICommand RepairConfigurationCommand { get; }
    public ICommand RefreshHistoryCommand { get; }
    public IReadOnlyList<HistoryProfileOption> HistoryProfiles => _historyProfiles;
    public IReadOnlyList<HistorySeverityOption> HistorySeverities { get; } =
        [new("All levels", null), new("Warnings and errors", EventSeverity.Warning),
         new("Errors", EventSeverity.Error)];
    public IReadOnlyList<HistoryKindOption> HistoryKinds { get; } =
        [new("All event types", null), .. Enum.GetValues<OperationalEventKind>()
            .Select(kind => new HistoryKindOption(SplitName(kind.ToString()), kind))];
    public IReadOnlyList<HistoryRangeOption> HistoryRanges { get; } =
        [new("Last 24 hours", TimeSpan.FromDays(1)),
         new("Last 7 days", TimeSpan.FromDays(7)), new("All retained", null)];
    public HistoryProfileOption SelectedHistoryProfile
    {
        get => _selectedHistoryProfile;
        set { _selectedHistoryProfile = value; PropertyChanged?.Invoke(this,
            new PropertyChangedEventArgs(nameof(SelectedHistoryProfile))); }
    }
    public HistorySeverityOption SelectedHistorySeverity
    {
        get => _selectedHistorySeverity;
        set { _selectedHistorySeverity = value; PropertyChanged?.Invoke(this,
            new PropertyChangedEventArgs(nameof(SelectedHistorySeverity))); }
    }
    public HistoryKindOption SelectedHistoryKind
    {
        get => _selectedHistoryKind;
        set { _selectedHistoryKind = value; PropertyChanged?.Invoke(this,
            new PropertyChangedEventArgs(nameof(SelectedHistoryKind))); }
    }
    public HistoryRangeOption SelectedHistoryRange
    {
        get => _selectedHistoryRange;
        set { _selectedHistoryRange = value; PropertyChanged?.Invoke(this,
            new PropertyChangedEventArgs(nameof(SelectedHistoryRange))); }
    }
    public IReadOnlyList<HistoryRow> HistoryRows => _historyRows;
    public string HistoryStatus => _historyStatus;
    public string HistorySummaryText => _historySummaryText;
    public bool HasHistory => _historyRows.Count > 0;
    public bool HasNoHistory => !HasHistory;
    public string HistoryEpisodeText
    {
        get => _historyEpisodeText;
        set { _historyEpisodeText = value; PropertyChanged?.Invoke(this,
            new PropertyChangedEventArgs(nameof(HistoryEpisodeText))); }
    }
    public IReadOnlyList<ApplicationFilterOption> ApplicationFilters { get; } =
        [new("All applications", null),
         new("Needs attention", ApplicationStatusCategory.Attention),
         new("Recovering", ApplicationStatusCategory.Recovering),
         new("Protected", ApplicationStatusCategory.Protected),
         new("Paused or disabled", ApplicationStatusCategory.PausedOrDisabled)];
    public IReadOnlyList<ApplicationSortOption> ApplicationSorts { get; } =
        [new("Name", ApplicationSortMode.Name),
         new("Status priority", ApplicationSortMode.Status),
         new("Most attempts", ApplicationSortMode.Attempts)];
    public ApplicationFilterOption SelectedApplicationFilter
    {
        get => _selectedApplicationFilter;
        set
        {
            if (value is null || value == _selectedApplicationFilter) return;
            _selectedApplicationFilter = value;
            RefreshVisibleApplications();
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SelectedApplicationFilter)));
        }
    }
    public ApplicationSortOption SelectedApplicationSort
    {
        get => _selectedApplicationSort;
        set
        {
            if (value is null || value == _selectedApplicationSort) return;
            _selectedApplicationSort = value;
            RefreshVisibleApplications();
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SelectedApplicationSort)));
        }
    }
    public IReadOnlyList<ApplicationStatusRow> ApplicationRows => _visibleApplicationRows;
    public bool HasApplications => _applicationRows.Count > 0;
    public bool HasNoApplications => !HasApplications;
    public bool HasNoVisibleApplications => HasApplications && _visibleApplicationRows.Count == 0;
    public string ApplicationCountText =>
        $"{_visibleApplicationRows.Count} shown of {_applicationRows.Count} configured";
    public string MonitoringBanner => _monitoringBanner;
    public bool CanRepairConfiguration => _canRepairConfiguration;
    public bool StartAtSignIn => _startAtSignIn;
    public bool CanChangeStartAtSignIn => _canChangeStartAtSignIn;
    public string StartAtSignInStatus => _startAtSignInStatus;
    public string FooterStatus => _footerStatus;
    public string TrayStatus => _trayStatus;
    public TrayIconState TrayIconState => _trayIconState;
    // Selection bindings also handle radio-button arrow keys and accessibility selection,
    // which can change IsChecked without invoking a button command.
    public bool IsApplications
    {
        get => _page == ShellPage.Applications;
        set { if (value) Navigate(ShellPage.Applications); }
    }
    public bool IsHistory
    {
        get => _page == ShellPage.History;
        set { if (value) Navigate(ShellPage.History); }
    }
    public bool IsSettings
    {
        get => _page == ShellPage.Settings;
        set { if (value) Navigate(ShellPage.Settings); }
    }
    public string Heading => _page switch
    {
        ShellPage.History => "History",
        ShellPage.Settings => "Settings",
        _ => "Applications"
    };
    public string Subtitle => _page switch
    {
        ShellPage.History => "A clear account of what happened while you were away.",
        ShellPage.Settings => "A quiet presence, on your terms.",
        _ => "A home for the apps you want to keep running."
    };
    public string DataDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Relight");

    public void UpdateMonitoring(string? configurationProblem, bool canRepairConfiguration,
        IReadOnlyList<HostedProfileStatus> profiles, EventRecorderStatus? logging,
        TimeSpan elapsed, DashboardEventHistory? eventHistory = null,
        string? eventHistoryProblem = null)
    {
        if (_canRepairConfiguration != canRepairConfiguration)
        {
            _canRepairConfiguration = canRepairConfiguration;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CanRepairConfiguration)));
        }
        ApplicationStatusRow[] rows = profiles
            .OrderBy(profile => profile.Name, StringComparer.CurrentCultureIgnoreCase)
            .Select(profile => new ApplicationStatusRow(profile.Id, profile.Name,
                StatusText(profile), profile.Problem ?? DetailText(profile),
                profile.Recovery?.Paused == true,
                profile.Recovery is not null && profile.AutomaticActionsAllowed &&
                    profile.Problem is null,
                profile.Recovery is { State: not Relight.Core.RecoveryState.Starting } recovery &&
                    (recovery.LockedOut || recovery.ReservedAutomaticAttempts > 0 ||
                     recovery.HoldReason is not null) &&
                    profile.AutomaticActionsAllowed && profile.Problem is null,
                profile.Recovery is { Paused: false, DetectionUnavailable: false,
                    HoldReason: null, State: Relight.Core.RecoveryState.WaitingForFirstStart or
                        Relight.Core.RecoveryState.RetryWaiting or
                        Relight.Core.RecoveryState.AwaitingIntervention } &&
                    profile.AutomaticActionsAllowed && profile.Problem is null,
                profile.TargetKind == TargetKind.Executable && profile.ConfiguredEnabled &&
                    profile.Recovery is { Enabled: true, DetectionUnavailable: false,
                        TargetIdentity: not null, State: not Relight.Core.RecoveryState.Starting } &&
                    profile.AutomaticActionsAllowed && profile.Problem is null,
                profile.TargetKind == TargetKind.Executable && profile.ConfiguredEnabled &&
                    profile.Recovery is { Enabled: true, DetectionUnavailable: false,
                        TargetIdentity: not null, HoldReason: null,
                        State: not Relight.Core.RecoveryState.Starting } &&
                    profile.AutomaticActionsAllowed && profile.Problem is null,
                profile.ConfiguredEnabled,
                configurationProblem is null &&
                    (profile.ConfiguredEnabled || profile.Problem is null),
                configurationProblem is null,
                configurationProblem is null,
                Category(profile), profile.Recovery?.ReservedAutomaticAttempts ?? 0,
                ProfileTimingPresentation.FromProfile(profile, elapsed),
                MilestoneText("Last recorded outage", eventHistory, eventHistoryProblem,
                    profile.Id, outage: true),
                MilestoneText("Last stable auto recovery", eventHistory, eventHistoryProblem,
                    profile.Id, outage: false)))
            .ToArray();
        bool structureChanged = !_applicationRows.Select(row => row.Id)
            .SequenceEqual(rows.Select(row => row.Id));
        if (structureChanged) _applicationRows = rows;
        else
            for (int i = 0; i < rows.Length; i++)
                _applicationRows[i].UpdateFrom(rows[i]);
        RefreshHistoryProfiles(_applicationRows);
        RefreshVisibleApplications();
        TraySnapshotSummary tray = TraySnapshotSummary.FromProfiles(profiles,
            configurationProblem is not null, logging?.Degraded == true);
        int active = tray.Protected;
        int attention = tray.Alerts;
        string banner = configurationProblem is not null
            ? $"Configuration needs attention: {configurationProblem}"
            : attention > 0
                ? $"{attention} application(s) need attention. Automatic recovery is suspended for them."
                : profiles.Count == 0
                    ? "No applications are configured. Monitoring is ready but idle."
                    : $"Monitoring {active} application(s). Closing this window keeps protection running.";
        if (logging?.Degraded == true)
            banner += " Event logging is degraded; review diagnostics before unattended use.";
        if (eventHistoryProblem is not null)
            banner += $" Dashboard event history is unavailable: {eventHistoryProblem}";
        else if (eventHistory?.SkippedMalformedLines > 0)
            banner += $" {eventHistory.SkippedMalformedLines} malformed history line(s) were skipped; displayed event times may be incomplete.";
        string footer = profiles.Count == 0
            ? "Relight is in the tray · No applications are configured"
            : $"Relight is in the tray · {active} protected · {attention} need attention";
        if (!structureChanged && banner == _monitoringBanner &&
            footer == _footerStatus && tray.Tooltip == _trayStatus &&
            tray.IconState == _trayIconState) return;
        _monitoringBanner = banner;
        _footerStatus = footer;
        _trayStatus = tray.Tooltip;
        _trayIconState = tray.IconState;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(null));
    }

    private static string MilestoneText(string label, DashboardEventHistory? history,
        string? problem, Guid profileId, bool outage)
    {
        if (problem is not null) return $"{label}: history unavailable";
        if (history is null) return $"{label}: loading history";
        history.Profiles.TryGetValue(profileId, out DashboardEventMilestones? milestones);
        DateTimeOffset? occurred = outage ? milestones?.LastOutageUtc :
            milestones?.LastAutomaticRecoveryUtc;
        return occurred is { } time
            ? $"{label}: {time.ToLocalTime():g}"
            : $"{label}: none in retained history";
    }

    public void ShowMonitoringProblem(string problem)
    {
        _canRepairConfiguration = false;
        _canChangeStartAtSignIn = false;
        _applicationRows = [];
        RefreshVisibleApplications();
        _monitoringBanner = $"Monitoring could not start: {problem}";
        _footerStatus = "Relight is in the tray · Monitoring unavailable";
        _trayStatus = "Relight · Monitoring unavailable";
        _trayIconState = TrayIconState.Attention;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(null));
    }

    public void UpdateStartAtSignIn(bool configured, bool canChange, string status)
    {
        if (_startAtSignIn == configured && _canChangeStartAtSignIn == canChange &&
            _startAtSignInStatus == status) return;
        _startAtSignIn = configured;
        _canChangeStartAtSignIn = canChange;
        _startAtSignInStatus = status;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(null));
    }

    private void RefreshVisibleApplications()
    {
        IEnumerable<ApplicationStatusRow> filtered = _selectedApplicationFilter.Category is { } category
            ? _applicationRows.Where(row => row.Category == category)
            : _applicationRows;
        filtered = _selectedApplicationSort.Mode switch
        {
            ApplicationSortMode.Status => filtered.OrderBy(row => row.Category)
                .ThenBy(row => row.Name, StringComparer.CurrentCultureIgnoreCase),
            ApplicationSortMode.Attempts => filtered.OrderByDescending(row => row.ReservedAttempts)
                .ThenBy(row => row.Name, StringComparer.CurrentCultureIgnoreCase),
            _ => filtered.OrderBy(row => row.Name, StringComparer.CurrentCultureIgnoreCase)
        };
        ApplicationStatusRow[] next = filtered.ToArray();
        if (_visibleApplicationRows.Select(row => row.Id).SequenceEqual(next.Select(row => row.Id)))
            return;
        _visibleApplicationRows = next;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ApplicationRows)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HasNoVisibleApplications)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ApplicationCountText)));
    }

    private static ApplicationStatusCategory Category(HostedProfileStatus profile)
    {
        if (profile.Problem is not null || profile.Recovery?.DetectionUnavailable == true ||
            profile.Recovery?.LockedOut == true || profile.Recovery?.HoldReason is not null)
            return ApplicationStatusCategory.Attention;
        if (!profile.ConfiguredEnabled || !profile.AutomaticActionsAllowed ||
            profile.Recovery?.Paused == true)
            return ApplicationStatusCategory.PausedOrDisabled;
        if (profile.Recovery?.State is Relight.Core.RecoveryState.Starting or
            Relight.Core.RecoveryState.Observing or Relight.Core.RecoveryState.RetryWaiting)
            return ApplicationStatusCategory.Recovering;
        return ApplicationStatusCategory.Protected;
    }

    public EventHistoryQuery CreateHistoryQuery(DateTimeOffset nowUtc)
    {
        Guid? episode = null;
        if (!string.IsNullOrWhiteSpace(_historyEpisodeText))
        {
            if (!Guid.TryParse(_historyEpisodeText.Trim(), out Guid parsed) ||
                parsed == Guid.Empty)
                throw new FormatException("Episode filter must be a valid nonempty ID.");
            episode = parsed;
        }
        return new(
            ProfileId: _selectedHistoryProfile.Id,
            MinimumSeverity: _selectedHistorySeverity.Minimum,
            Kind: _selectedHistoryKind.Kind,
            EpisodeId: episode,
            FromUtc: _selectedHistoryRange.Lookback is { } span ? nowUtc - span : null);
    }

    public void ShowHistoryLoading()
    {
        _historyRows = [];
        _historyStatus = "Loading local event history…";
        _historySummaryText = "Calculating the selected period from retained events…";
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(null));
    }

    public void ShowHistoryProblem(string problem)
    {
        _historyStatus = $"History could not be loaded: {problem}";
        _historySummaryText = "Summary unavailable.";
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(null));
    }

    public void UpdateHistory(EventHistoryOverview overview)
    {
        EventHistoryResult result = overview.Results;
        EventHistorySummary summary = overview.Summary;
        _retainedHistoryProfiles = overview.Profiles;
        RefreshHistoryProfiles(_applicationRows);
        Dictionary<Guid, string> currentNames = _retainedHistoryProfiles
            .Where(profile => !string.IsNullOrWhiteSpace(profile.Name))
            .ToDictionary(profile => profile.Id, profile => profile.Name!);
        foreach (ApplicationStatusRow row in _applicationRows)
            currentNames[row.Id] = row.Name;
        _historyRows = result.Events.Select(entry => new HistoryRow(
            entry.OccurredUtc.ToLocalTime().ToString("g"),
            entry.OccurredUtc.ToString("u"),
            entry.ProfileName ?? (entry.ProfileId is { } profileId &&
                currentNames.TryGetValue(profileId, out string? currentName)
                    ? currentName : entry.ProfileId?.ToString() ?? "Relight"),
            entry.Severity.ToString(), SplitName(entry.Kind.ToString()),
            FormatSummary(entry), FormatDetails(entry))).ToArray();
        _historyStatus = result.TotalMatches == 0
            ? "No recorded events match these filters."
            : result.Truncated
                ? $"Showing the latest {_historyRows.Count} of {result.TotalMatches} matching events. Narrow the filters to see older events."
                : $"Showing {result.TotalMatches} matching event(s).";
        if (result.SkippedMalformedLines > 0)
            _historyStatus += $" {result.SkippedMalformedLines} damaged log line(s) could not be read.";
        _historySummaryText =
            $"Selected period: {summary.ObservedDisappearances} observed disappearance(s), " +
            $"{summary.AutomaticAttemptsReserved} automatic attempt(s) reserved " +
            $"({summary.AutomaticLaunchesDispatched} dispatched), " +
            $"{summary.StableAutomaticRecoveries} stable automatic recovery(ies), " +
            $"{summary.OtherStableStarts} other stable start(s), " +
            $"and {summary.Lockouts} lockout(s). " +
            $"Monitoring gaps: {summary.MonitoringGaps} reported, " +
            $"{summary.MonitoringRestorations} restoration(s) observed. " +
            "Gap time is unknown and is never counted as confirmed application downtime.";
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(null));
    }

    private void RefreshHistoryProfiles(IReadOnlyList<ApplicationStatusRow> currentRows)
    {
        Guid? selectedId = _selectedHistoryProfile.Id;
        var names = _retainedHistoryProfiles.ToDictionary(profile => profile.Id,
            profile => profile.Name is { Length: > 0 } name
                ? $"{name} (removed)" : $"{profile.Id} (removed)");
        foreach (ApplicationStatusRow row in currentRows)
            names[row.Id] = row.Name;
        HistoryProfileOption[] availableProfiles =
            [new("All applications", null), .. names
                .OrderBy(item => item.Value, StringComparer.CurrentCultureIgnoreCase)
                .Select(item => new HistoryProfileOption(item.Value, item.Key))];
        if (_historyProfiles.SequenceEqual(availableProfiles)) return;
        _historyProfiles = availableProfiles;
        _selectedHistoryProfile = availableProfiles.FirstOrDefault(option =>
            option.Id == selectedId) ?? availableProfiles[0];
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(null));
    }

    private static string FormatSummary(OperationalEvent entry)
    {
        string transition = entry.PreviousState is { } before &&
            entry.NewState is { } after ? $" · {SplitName(before.ToString())} → {SplitName(after.ToString())}" : "";
        string attempt = entry.AttemptNumber is { } number
            ? $" · attempt {number}" + (entry.AttemptLimit is { } limit ? $" of {limit}" : "") : "";
        return $"{SplitName(entry.Kind.ToString())}{transition}{attempt}";
    }

    private static string FormatDetails(OperationalEvent entry) =>
        $"UTC: {entry.OccurredUtc:u}\nEvent ID: {entry.EventId}\n" +
        $"Profile ID: {entry.ProfileId?.ToString() ?? "—"}\n" +
        $"Episode ID: {entry.EpisodeId?.ToString() ?? "—"}\n" +
        $"Operation ID: {entry.OperationId?.ToString() ?? "—"}\n" +
        $"Origin: {entry.Origin?.ToString() ?? "—"}\n" +
        $"Process identity: {entry.ProcessIdentity ?? "—"}\n" +
        $"Native error code: {entry.NativeErrorCode?.ToString() ?? "—"}";

    private static string SplitName(string value) =>
        System.Text.RegularExpressions.Regex.Replace(value, "(?<!^)([A-Z])", " $1");

    private static string StatusText(HostedProfileStatus profile)
    {
        if (!profile.Monitoring) return profile.Problem is null ? "Disabled" : "Unavailable";
        if (profile.Recovery?.Paused == true) return "Paused";
        if (profile.Recovery?.DetectionUnavailable == true) return "Detection unavailable";
        if (!profile.AutomaticActionsAllowed) return "Detection only";
        return profile.Recovery?.State switch
        {
            Relight.Core.RecoveryState.WaitingForFirstStart => "Waiting for first start",
            Relight.Core.RecoveryState.RetryWaiting => "Waiting to retry",
            Relight.Core.RecoveryState.AwaitingIntervention => "Needs intervention",
            Relight.Core.RecoveryState.Starting => "Starting",
            Relight.Core.RecoveryState.Observing => "Observing stability",
            Relight.Core.RecoveryState.Healthy => "Healthy",
            Relight.Core.RecoveryState.Disabled => "Disabled",
            _ => "Starting monitoring"
        };
    }

    private static string DetailText(HostedProfileStatus profile) =>
        !profile.ConfiguredEnabled
            ? profile.Recovery is { } disabled
                ? $"Protection is disabled; the target is left running. {disabled.ReservedAutomaticAttempts} automatic attempt(s) remain charged."
                : "Protection is disabled; the target is left running."
        : profile.Recovery is { } recovery
            ? recovery.Paused
                ? $"Protection is paused; the application is left running. {recovery.ReservedAutomaticAttempts} automatic attempt(s) remain charged."
                : recovery.HoldReason == Relight.Core.RecoveryHoldReason.InterruptedExplicitLaunch
                    ? "A previous Start now was interrupted. Its outcome is unknown, so automatic recovery is suspended. Verify the app before resetting recovery."
                : recovery.LockedOut
                    ? $"Automatic recovery is locked after {recovery.ReservedAutomaticAttempts} attempt(s). Reset recovery is an explicit choice."
                    : $"{recovery.ReservedAutomaticAttempts} automatic attempt(s) reserved in this episode."
            : profile.Detection?.Kind.ToString() ?? "No process result yet.";

    public void Navigate(ShellPage page)
    {
        if (_page == page)
        {
            if (page == ShellPage.History)
                HistoryRefreshRequested?.Invoke(this, EventArgs.Empty);
            return;
        }
        _page = page;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(null));
        if (page == ShellPage.History)
            HistoryRefreshRequested?.Invoke(this, EventArgs.Empty);
    }

    public void ShowHistoryFor(Guid profileId)
    {
        SelectedHistoryProfile = _historyProfiles.FirstOrDefault(option =>
            option.Id == profileId) ?? _historyProfiles[0];
        Navigate(ShellPage.History);
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    public event EventHandler? HistoryRefreshRequested;
}

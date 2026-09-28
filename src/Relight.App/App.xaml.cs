using System;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Win32;
using Relight.Engine;
using Relight.Services;
using Relight.Storage;
using Relight.ViewModels;
using Relight.Windows;

namespace Relight;

public partial class App : Application
{
    private SingleInstanceService? _instance;
    private TrayService? _tray;
    private MainWindow? _dashboard;
    private ShellViewModel? _viewModel;
    private CancellationTokenSource? _monitoringCancellation;
    private Task? _monitoringTask;
    private RecoveryApplicationHost? _host;
    private DispatcherTimer? _statusTimer;
    private bool _updatingStatus;
    private bool _exiting;
    private bool _repairingConfiguration;
    private bool _changingStartup;
    private bool _changingAllPause;
    private int _reconcilingWindows;
    private CurrentUserStartupRegistration? _startupRegistration;
    private string? _startupUnavailable;
    private CancellationTokenSource? _historyCancellation;
    private DashboardEventHistory? _dashboardHistory;
    private string? _dashboardHistoryProblem;
    private DateTimeOffset _nextDashboardHistoryRefreshUtc;
    private readonly RecoveryNotificationPlanner _notificationPlanner = new();
    private string? _notificationProblem;
    private string? _sharedSessionPreviewDataDirectory;

    private static string? PreviewDirectory(string[] args)
    {
        int index = Array.IndexOf(args, "--shared-session-preview");
        if (index < 0) return null;
        if (Array.LastIndexOf(args, "--shared-session-preview") != index ||
            index + 1 >= args.Length || !Path.IsPathFullyQualified(args[index + 1]) ||
            Array.IndexOf(args, "--shell-test") >= 0)
            throw new ArgumentException(
                "Use --shared-session-preview <absolute-data-directory> without --shell-test.");
        return Path.GetFullPath(args[index + 1]);
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        try
        {
            _sharedSessionPreviewDataDirectory = PreviewDirectory(e.Args);
            _instance = new SingleInstanceService(_sharedSessionPreviewDataDirectory);
            if (!_instance.IsPrimary)
            {
                _instance.ActivatePrimary();
                Shutdown();
                return;
            }

            ApplyAccessibilityColors();
            SystemParameters.StaticPropertyChanged += OnSystemParametersChanged;
            try
            {
                if (_sharedSessionPreviewDataDirectory is null)
                    _startupRegistration = new CurrentUserStartupRegistration(
                        Environment.ProcessPath ?? throw new InvalidOperationException(
                            "The Relight executable path is unavailable."));
                else
                    _startupUnavailable = "Sign-in startup is unavailable in shared-session preview.";
            }
            catch (Exception error) when (error is ArgumentException or InvalidOperationException)
            {
                _startupUnavailable = error.Message;
            }
            _viewModel = new ShellViewModel(HideDashboard, RequestExit, ShowAddApplication,
                RepairConfiguration, _sharedSessionPreviewDataDirectory);
            _viewModel.HistoryRefreshRequested += OnHistoryRefreshRequested;
            _dashboard = new MainWindow(SetProfilePausedAsync, ResetProfileRecoveryAsync,
                RepairRecoveryStateAsync, ReplaceUnavailableProfileAsync,
                StartProfileNowAsync, ExportHistoryAsync, SetProfileEnabledAsync,
                RemoveProfileAsync, DuplicateProfileAsync, ShowEditProfile,
                SetStartAtSignInAsync,
                ExportDiagnosticsAsync, StopProfileAndPauseAsync,
                ForceClosePausedProfileAsync, StopProfileForRestartAsync,
                CompleteProfileRestartAsync)
            {
                DataContext = _viewModel
            };
            if (_sharedSessionPreviewDataDirectory is not null)
                _dashboard.Title = "Relight — Shared-session preview";
            MainWindow = _dashboard;
            _dashboard.Closing += OnDashboardClosing;
            _tray = new TrayService(
                () => ShowDashboard(ShellPage.Applications),
                ShowAddApplication,
                () => ShowDashboard(ShellPage.History),
                () => ChangeAllPauseFromTray(paused: true),
                () => ChangeAllPauseFromTray(paused: false),
                ToggleStartupFromTray,
                RequestExit);
            _instance.Listen(() => Dispatcher.BeginInvoke(() => ShowDashboard()));

            if (Array.IndexOf(e.Args, "--shell-test") >= 0)
            {
                _viewModel.ShowMonitoringProblem("Shell test mode: monitoring is disabled.");
                _tray.UpdateStatus("Relight · Shell test mode", TrayIconState.Paused);
            }
            else
            {
                _monitoringCancellation = new CancellationTokenSource();
                _monitoringTask = Task.Run(() => RunMonitoringAsync(_monitoringCancellation.Token));
                _statusTimer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher)
                {
                    Interval = TimeSpan.FromSeconds(1)
                };
                _statusTimer.Tick += OnStatusTick;
                _statusTimer.Start();
                SystemEvents.PowerModeChanged += OnPowerModeChanged;
                SystemEvents.SessionSwitch += OnSessionSwitch;
            }

            if (Array.IndexOf(e.Args, "--tray") < 0)
            {
                ShowDashboard();
            }
        }
        catch (Exception error)
        {
            MessageBox.Show($"Relight could not start.\n\n{error.Message}", "Relight startup problem",
                MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
        }
    }

    private async Task RunMonitoringAsync(CancellationToken cancellationToken)
    {
        RecoveryApplicationHost? host = null;
        try
        {
            host = _sharedSessionPreviewDataDirectory is { } previewDirectory
                ? await RecoveryApplicationHost.OpenSharedSessionPreviewAsync(previewDirectory,
                    cancellationToken).ConfigureAwait(false)
                : await RecoveryApplicationHost.OpenAsync(cancellationToken: cancellationToken)
                    .ConfigureAwait(false);
            Volatile.Write(ref _host, host);
            await host.RunAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception error)
        {
            if (!cancellationToken.IsCancellationRequested)
                await Dispatcher.InvokeAsync(() => SetMonitoringProblem(error.Message));
        }
        finally
        {
            Volatile.Write(ref _host, null);
            if (host is not null)
            {
                try { await host.DisposeAsync().ConfigureAwait(false); }
                catch (Exception error) when (!cancellationToken.IsCancellationRequested)
                {
                    await Dispatcher.InvokeAsync(() => SetMonitoringProblem(error.Message));
                }
            }
        }
    }

    private async void OnStatusTick(object? sender, EventArgs e)
    {
        if (_updatingStatus || _exiting || _repairingConfiguration) return;
        RecoveryApplicationHost? host = Volatile.Read(ref _host);
        if (host is null) return;
        _updatingStatus = true;
        try
        {
            var profiles = host.GetProfiles();
            foreach (OperationalEvent entry in host.DrainNotificationEvents())
            {
                ProfileConfiguration? profile = host.Configuration?.Configuration.Profiles
                    .FirstOrDefault(item => item.Id == entry.ProfileId);
                RecoveryNotification? notice = _notificationPlanner.Plan(entry, profile);
                if (notice is null || _tray is null) continue;
                try
                {
                    _tray.ShowRecoveryNotification(notice.Title, notice.Message,
                        notice.Warning);
                    _notificationProblem = null;
                }
                catch (Exception error)
                {
                    _notificationProblem = $"Tray notification could not be shown: {error.Message}";
                }
            }
            var logging = await host.GetLoggingStatusAsync();
            if (DateTimeOffset.UtcNow >= _nextDashboardHistoryRefreshUtc)
            {
                _nextDashboardHistoryRefreshUtc = DateTimeOffset.UtcNow.AddSeconds(30);
                try
                {
                    _dashboardHistory = await new OperationalEventHistoryReader(
                        _viewModel?.DataDirectory ?? RecoveryApplicationHost.DefaultDataDirectory)
                        .ReadDashboardMilestonesAsync(_monitoringCancellation?.Token ??
                            CancellationToken.None);
                    _dashboardHistoryProblem = null;
                }
                catch (OperationCanceledException) when (_exiting) { return; }
                catch (Exception error)
                {
                    _dashboardHistory = null;
                    _dashboardHistoryProblem = error.Message;
                }
            }
            StartupRegistrationStatus? startupStatus = null;
            string? startupProblem = _startupUnavailable;
            if (_startupRegistration is not null)
            {
                try
                {
                    startupStatus = await Task.Run(() => _startupRegistration.Inspect());
                }
                catch (Exception error)
                {
                    startupProblem = $"Sign-in startup could not be checked: {error.Message}";
                }
            }
            _viewModel?.UpdateMonitoring(host.ConfigurationProblem,
                host.Configuration?.FromLastGoodBackup == true, profiles, logging,
                host.Elapsed, _dashboardHistory, _dashboardHistoryProblem,
                _notificationProblem);
            bool configured = host.Configuration?.Configuration.Settings.StartAtSignIn == true;
            bool registered = startupStatus?.EnabledForThisExecutable == true;
            bool startupAvailable = host.Configuration?.AutomaticActionsAllowed == true &&
                startupStatus is { ConflictingValue: false } && !_changingStartup;
            string explanation = startupProblem ?? (startupStatus switch
            {
                { ConflictingValue: true } =>
                    "Another startup entry named Relight exists; it was left untouched.",
                { RegisteredToAnotherRelightExecutable: true } =>
                    "An older Relight.exe path is registered. Turn this on to use the current path.",
                _ when configured != registered =>
                    "Saved preference and Windows registration differ. Use this control to reconcile them.",
                _ when registered => "Relight will start in the tray at your next sign-in.",
                _ => "Off · Relight will not start automatically at sign-in."
            });
            _viewModel?.UpdateStartAtSignIn(registered, startupAvailable, explanation);
            if (_viewModel is not null)
                _tray?.UpdateStatus(_viewModel.TrayStatus, _viewModel.TrayIconState);
            _tray?.UpdateStartupStatus(registered, startupAvailable, explanation);
            bool canPause = !_changingAllPause && profiles.Any(profile =>
                profile.AutomaticActionsAllowed && profile.Problem is null &&
                profile.Recovery is { Enabled: true, Paused: false });
            bool canResume = !_changingAllPause && profiles.Any(profile =>
                profile.AutomaticActionsAllowed && profile.Problem is null &&
                profile.Recovery is { Enabled: true, Paused: true });
            _tray?.UpdatePauseAvailability(canPause, canResume);
        }
        catch (Exception error)
        {
            SetMonitoringProblem(error.Message);
        }
        finally { _updatingStatus = false; }
    }

    private void SetMonitoringProblem(string message)
    {
        _viewModel?.ShowMonitoringProblem(message);
        _tray?.UpdateStatus("Relight · Monitoring unavailable", TrayIconState.Attention);
    }

    private async Task SetStartAtSignInAsync(bool enabled)
    {
        if (_changingStartup) throw new InvalidOperationException(
            "A sign-in startup change is already in progress.");
        CurrentUserStartupRegistration startup = _startupRegistration ??
            throw new InvalidOperationException(_startupUnavailable ??
                "Sign-in startup is unavailable.");
        RecoveryApplicationHost host = Volatile.Read(ref _host) ??
            throw new InvalidOperationException("Monitoring is unavailable.");
        _changingStartup = true;
        try
        {
            await host.SetStartAtSignInAsync(enabled, startup,
                _monitoringCancellation?.Token ?? CancellationToken.None);
        }
        finally { _changingStartup = false; }
    }

    private async void ToggleStartupFromTray()
    {
        if (_exiting || _changingStartup || _viewModel is null) return;
        try { await SetStartAtSignInAsync(!_viewModel.StartAtSignIn); }
        catch (Exception error)
        {
            ShowDashboard(ShellPage.Settings);
            MessageBox.Show(_dashboard!, error.Message, "Could not change sign-in startup",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private async void ChangeAllPauseFromTray(bool paused)
    {
        if (_exiting || _changingAllPause) return;
        RecoveryApplicationHost? host = Volatile.Read(ref _host);
        if (host is null) return;
        _changingAllPause = true;
        _tray?.UpdatePauseAvailability(false, false);
        try
        {
            ProfileBatchResult result = await host.SetAllPausedAsync(paused,
                _monitoringCancellation?.Token ?? CancellationToken.None);
            if (result.Errors.Count > 0)
            {
                ShowDashboard(ShellPage.Applications);
                string errors = string.Join("\n", result.Errors.Take(8));
                if (result.Errors.Count > 8)
                    errors += $"\n…and {result.Errors.Count - 8} more. Check application status.";
                MessageBox.Show(_dashboard!,
                    $"Updated {result.Completed} of {result.Requested} profiles.\n\n" +
                    errors,
                    paused ? "Pause all partially completed" : "Resume all partially completed",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }
        catch (Exception error)
        {
            if (!_exiting)
            {
                ShowDashboard(ShellPage.Applications);
                MessageBox.Show(_dashboard!, error.Message,
                    paused ? "Could not pause all" : "Could not resume all",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }
        finally { _changingAllPause = false; }
    }

    private async void RepairConfiguration()
    {
        if (_repairingConfiguration || _exiting || _dashboard is null) return;
        RecoveryApplicationHost? host = Volatile.Read(ref _host);
        StoredConfiguration? fallback = host?.Configuration;
        if (host is null || fallback?.FromLastGoodBackup != true) return;
        string profileCount = fallback.Configuration.Profiles.Count == 1
            ? "1 profile" : $"{fallback.Configuration.Profiles.Count} profiles";
        if (MessageBox.Show(_dashboard,
                $"Restore last-good configuration revision {fallback.Revision} " +
                $"with {profileCount}?\n\nThe invalid current file will be preserved. " +
                "Monitoring will restart using the restored settings; automatic recovery " +
                "may resume for profiles with valid recovery state. Running applications " +
                "will be left alone.",
                "Restore last-good configuration?", MessageBoxButton.OKCancel,
                MessageBoxImage.Warning, MessageBoxResult.Cancel) != MessageBoxResult.OK)
            return;
        _repairingConfiguration = true;
        try
        {
            string? preserved = await host.RepairConfigurationAsync(
                _monitoringCancellation?.Token ?? CancellationToken.None);
            SetMonitoringProblem("Configuration restored; reopening monitoring from disk.");
            CancellationTokenSource? oldCancellation = _monitoringCancellation;
            Task? oldTask = _monitoringTask;
            oldCancellation?.Cancel();
            if (oldTask is not null) await oldTask;
            oldCancellation?.Dispose();
            if (_exiting) return;
            _monitoringCancellation = new CancellationTokenSource();
            CancellationToken monitoringToken = _monitoringCancellation.Token;
            _monitoringTask = Task.Run(() => RunMonitoringAsync(monitoringToken));
            MessageBox.Show(_dashboard,
                "Last-good configuration restored. Monitoring is restarting. " +
                (preserved is null ? "No invalid current file existed to preserve."
                    : $"The invalid file was preserved at:\n{preserved}"),
                "Configuration restored", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception error)
        {
            if (!_exiting)
                MessageBox.Show(_dashboard, error.Message, "Configuration repair failed",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally { _repairingConfiguration = false; }
    }

    private async void OnHistoryRefreshRequested(object? sender, EventArgs e)
    {
        if (_exiting || _viewModel is null) return;
        _historyCancellation?.Cancel();
        var cancellation = CancellationTokenSource.CreateLinkedTokenSource(
            _monitoringCancellation?.Token ?? CancellationToken.None);
        _historyCancellation = cancellation;
        _viewModel.ShowHistoryLoading();
        try
        {
            EventHistoryQuery query = _viewModel.CreateHistoryQuery(DateTimeOffset.UtcNow);
            string dataDirectory = _viewModel.DataDirectory;
            EventHistoryOverview result = await Task.Run(() =>
                new OperationalEventHistoryReader(dataDirectory)
                    .ReadOverviewAsync(query, cancellation.Token), cancellation.Token);
            if (!cancellation.IsCancellationRequested)
                _viewModel.UpdateHistory(result);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        catch (Exception error)
        {
            if (!cancellation.IsCancellationRequested)
                _viewModel.ShowHistoryProblem(error.Message);
        }
        finally
        {
            if (ReferenceEquals(_historyCancellation, cancellation))
                _historyCancellation = null;
            cancellation.Dispose();
        }
    }

    private void ShowDashboard(ShellPage? page = null)
    {
        if (_exiting || _dashboard is null || _viewModel is null)
        {
            return;
        }

        if (page.HasValue)
        {
            _viewModel.Navigate(page.Value);
        }

        _dashboard.Show();
        if (_dashboard.WindowState == WindowState.Minimized)
        {
            _dashboard.WindowState = WindowState.Normal;
        }

        _dashboard.Activate();
    }

    private void ShowAddApplication()
    {
        if (_exiting || _dashboard is null) return;
        ShowDashboard(ShellPage.Applications);
        RecoveryApplicationHost? host = Volatile.Read(ref _host);
        if (host is null)
        {
            MessageBox.Show(_dashboard,
                "Monitoring is still loading or unavailable. Check the status banner, then try again.",
                "Relight", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var dialog = new AddApplicationWindow(host) { Owner = _dashboard };
        dialog.ShowDialog();
    }

    private void ShowEditProfile(Guid profileId)
    {
        if (_exiting || _dashboard is null) return;
        RecoveryApplicationHost? host = Volatile.Read(ref _host);
        if (host is null)
        {
            MessageBox.Show(_dashboard, "Monitoring is unavailable.", "Relight",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        try
        {
            var dialog = new EditProfileWindow(host.GetProfileForEdit(profileId),
                (id, name, target, policy, recovery, lockout) =>
                    target.Kind == TargetKind.Executable
                        ? host.UpdateProfileDefinitionAsync(id, name, target, policy,
                            recovery, lockout,
                            _monitoringCancellation?.Token ?? CancellationToken.None)
                        : host.UpdateProfileSettingsAsync(id, name, policy,
                            recovery, lockout,
                            _monitoringCancellation?.Token ?? CancellationToken.None),
                (id, cancellationToken) => host.TestProfileLaunchAsync(id,
                    cancellationToken))
            {
                Owner = _dashboard
            };
            dialog.ShowDialog();
        }
        catch (Exception error)
        {
            MessageBox.Show(_dashboard, error.Message, "Could not open profile editor",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private Task SetProfilePausedAsync(Guid profileId, bool paused)
    {
        RecoveryApplicationHost host = Volatile.Read(ref _host) ??
            throw new InvalidOperationException("Monitoring is unavailable.");
        return host.SetProfilePausedAsync(profileId, paused,
            _monitoringCancellation?.Token ?? CancellationToken.None);
    }

    private Task ResetProfileRecoveryAsync(Guid profileId)
    {
        RecoveryApplicationHost host = Volatile.Read(ref _host) ??
            throw new InvalidOperationException("Monitoring is unavailable.");
        return host.ResetProfileRecoveryAsync(profileId,
            _monitoringCancellation?.Token ?? CancellationToken.None);
    }

    private Task RepairRecoveryStateAsync(Guid profileId)
    {
        RecoveryApplicationHost host = Volatile.Read(ref _host) ??
            throw new InvalidOperationException("Monitoring is unavailable.");
        return host.RepairUnavailableRecoveryStateAsync(profileId,
            _monitoringCancellation?.Token ?? CancellationToken.None);
    }

    private Task<Guid> ReplaceUnavailableProfileAsync(Guid profileId)
    {
        RecoveryApplicationHost host = Volatile.Read(ref _host) ??
            throw new InvalidOperationException("Monitoring is unavailable.");
        return host.ReplaceUnavailableProfileAsync(profileId,
            _monitoringCancellation?.Token ?? CancellationToken.None);
    }

    private Task StartProfileNowAsync(Guid profileId)
    {
        RecoveryApplicationHost host = Volatile.Read(ref _host) ??
            throw new InvalidOperationException("Monitoring is unavailable.");
        return host.StartProfileNowAsync(profileId,
            _monitoringCancellation?.Token ?? CancellationToken.None);
    }

    private Task<StopCommandResult> StopProfileAndPauseAsync(Guid profileId)
    {
        RecoveryApplicationHost host = Volatile.Read(ref _host) ??
            throw new InvalidOperationException("Monitoring is unavailable.");
        return host.StopProfileAndPauseAsync(profileId, TimeSpan.FromSeconds(10),
            _monitoringCancellation?.Token ?? CancellationToken.None);
    }

    private Task<TargetStopResult> ForceClosePausedProfileAsync(Guid profileId,
        Guid operationId, string selectedIdentity)
    {
        RecoveryApplicationHost host = Volatile.Read(ref _host) ??
            throw new InvalidOperationException("Monitoring is unavailable.");
        return host.ForceClosePausedProfileAsync(profileId, operationId, selectedIdentity,
            _monitoringCancellation?.Token ?? CancellationToken.None);
    }

    private Task<StopCommandResult> StopProfileForRestartAsync(Guid profileId)
    {
        RecoveryApplicationHost host = Volatile.Read(ref _host) ??
            throw new InvalidOperationException("Monitoring is unavailable.");
        return host.StopProfileForRestartAsync(profileId, TimeSpan.FromSeconds(10),
            _monitoringCancellation?.Token ?? CancellationToken.None);
    }

    private Task<CoordinatorResult> CompleteProfileRestartAsync(Guid profileId,
        Guid operationId, string? selectedIdentity)
    {
        RecoveryApplicationHost host = Volatile.Read(ref _host) ??
            throw new InvalidOperationException("Monitoring is unavailable.");
        return host.CompleteProfileRestartAsync(profileId, operationId, selectedIdentity,
            _monitoringCancellation?.Token ?? CancellationToken.None);
    }

    private Task SetProfileEnabledAsync(Guid profileId, bool enabled)
    {
        RecoveryApplicationHost host = Volatile.Read(ref _host) ??
            throw new InvalidOperationException("Monitoring is unavailable.");
        return host.SetProfileEnabledAsync(profileId, enabled,
            _monitoringCancellation?.Token ?? CancellationToken.None);
    }

    private Task RemoveProfileAsync(Guid profileId)
    {
        RecoveryApplicationHost host = Volatile.Read(ref _host) ??
            throw new InvalidOperationException("Monitoring is unavailable.");
        return host.RemoveProfileAsync(profileId,
            _monitoringCancellation?.Token ?? CancellationToken.None);
    }

    private Task<Guid> DuplicateProfileAsync(Guid profileId)
    {
        RecoveryApplicationHost host = Volatile.Read(ref _host) ??
            throw new InvalidOperationException("Monitoring is unavailable.");
        return host.DuplicateProfileAsync(profileId,
            _monitoringCancellation?.Token ?? CancellationToken.None);
    }

    private Task<EventHistoryExportResult> ExportHistoryAsync(EventHistoryQuery query,
        string destination, EventHistoryExportFormat format)
    {
        if (_exiting) throw new InvalidOperationException("Relight is exiting.");
        string directory = _viewModel?.DataDirectory ??
            throw new InvalidOperationException("History is unavailable.");
        CancellationToken cancellationToken = _monitoringCancellation?.Token ??
            CancellationToken.None;
        return Task.Run(() => new OperationalEventHistoryReader(directory)
            .ExportAsync(query, destination, format, cancellationToken), cancellationToken);
    }

    private async Task<DiagnosticBundleResult> ExportDiagnosticsAsync(string destination)
    {
        if (_exiting) throw new InvalidOperationException("Relight is exiting.");
        RecoveryApplicationHost? host = Volatile.Read(ref _host);
        string directory = _viewModel?.DataDirectory ??
            throw new InvalidOperationException("Diagnostics are unavailable.");
        CancellationToken cancellationToken = _monitoringCancellation?.Token ??
            CancellationToken.None;
        EventRecorderStatus? logging = host is null ? null :
            await host.GetLoggingStatusAsync(cancellationToken);
        StoredConfiguration? configuration = host?.Configuration;
        DiagnosticTargetStatus[]? targets = host?.GetProfiles().Select(profile =>
            new DiagnosticTargetStatus(profile.Id, profile.TargetKind,
                profile.Monitoring, profile.AutomaticActionsAllowed,
                profile.Detection?.Kind, profile.Recovery?.State,
                profile.Recovery?.DetectionUnavailable, profile.Recovery?.LockedOut,
                profile.Problem is not null)).ToArray();
        string version = typeof(App).Assembly.GetName().Version?.ToString() ?? "unknown";
        return await Task.Run(() => new DiagnosticBundleExporter(directory)
            .ExportAsync(destination, configuration, logging, version,
                cancellationToken, targets),
            cancellationToken);
    }

    private void HideDashboard() => _dashboard?.Hide();

    private void OnDashboardClosing(object? sender, CancelEventArgs e)
    {
        if (!_exiting)
        {
            e.Cancel = true;
            HideDashboard();
        }
    }

    private void RequestExit()
    {
        if (_exiting)
        {
            return;
        }

        ShowDashboard();
        if (MessageBox.Show(_dashboard!,
                "Relight will leave the system tray. Other applications will keep running.",
                "Exit Relight?", MessageBoxButton.OKCancel, MessageBoxImage.Information,
                MessageBoxResult.Cancel) != MessageBoxResult.OK)
        {
            return;
        }

        _exiting = true;
        _ = CompleteExitAsync();
    }

    private async Task CompleteExitAsync()
    {
        _statusTimer?.Stop();
        _historyCancellation?.Cancel();
        _monitoringCancellation?.Cancel();
        try
        {
            if (_monitoringTask is not null) await _monitoringTask;
        }
        catch (Exception error)
        {
            MessageBox.Show(_dashboard!, $"Relight could not finish monitoring cleanly.\n\n{error.Message}",
                "Relight exit problem", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally { Shutdown(); }
    }

    private void OnSystemParametersChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SystemParameters.HighContrast))
        {
            Dispatcher.Invoke(ApplyAccessibilityColors);
        }
    }

    private void OnPowerModeChanged(object? sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode == PowerModes.Resume) ReconcileAfterWindowsInterruption();
    }

    private void OnSessionSwitch(object? sender, SessionSwitchEventArgs e)
    {
        if (e.Reason == SessionSwitchReason.SessionUnlock)
            ReconcileAfterWindowsInterruption();
    }

    private void ReconcileAfterWindowsInterruption()
    {
        RecoveryApplicationHost? host = Volatile.Read(ref _host);
        if (_exiting || host is null ||
            Interlocked.Exchange(ref _reconcilingWindows, 1) != 0) return;
        CancellationToken cancellationToken = _monitoringCancellation?.Token ??
            CancellationToken.None;
        Task reconciliation = host.ReconcileAfterWindowsInterruptionAsync(cancellationToken);
        _ = ObserveWindowsReconciliationAsync(reconciliation, cancellationToken);
    }

    private async Task ObserveWindowsReconciliationAsync(Task reconciliation,
        CancellationToken cancellationToken)
    {
        try
        {
            await reconciliation.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception error)
        {
            if (!_exiting)
                await Dispatcher.InvokeAsync(() => SetMonitoringProblem(error.Message));
        }
        finally { Interlocked.Exchange(ref _reconcilingWindows, 0); }
    }

    private void ApplyAccessibilityColors()
    {
        // Local overrides preserve native contrast preferences without custom control templates.
        string[] keys = ["CanvasBrush", "SurfaceBrush", "TextBrush", "MutedBrush", "LineBrush",
            "AccentBrush", "AccentSoftBrush"];
        foreach (string key in keys)
        {
            Resources.Remove(key);
        }

        if (SystemParameters.HighContrast)
        {
            Resources["CanvasBrush"] = SystemColors.WindowBrush;
            Resources["SurfaceBrush"] = SystemColors.WindowBrush;
            Resources["TextBrush"] = SystemColors.WindowTextBrush;
            Resources["MutedBrush"] = SystemColors.WindowTextBrush;
            Resources["LineBrush"] = SystemColors.WindowTextBrush;
            Resources["AccentBrush"] = SystemColors.WindowTextBrush;
            Resources["AccentSoftBrush"] = SystemColors.WindowBrush;
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _exiting = true;
        _statusTimer?.Stop();
        _monitoringCancellation?.Cancel();
        CancellationTokenSource? cancellation = _monitoringCancellation;
        if (_monitoringTask is { IsCompleted: false } task)
            _ = task.ContinueWith(_ => cancellation?.Dispose(), TaskScheduler.Default);
        else cancellation?.Dispose();
        SystemParameters.StaticPropertyChanged -= OnSystemParametersChanged;
        SystemEvents.PowerModeChanged -= OnPowerModeChanged;
        SystemEvents.SessionSwitch -= OnSessionSwitch;
        _tray?.Dispose();
        _instance?.Dispose();
        base.OnExit(e);
    }
}

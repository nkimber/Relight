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

internal sealed record ApplicationStatusRow(Guid Id, string Name, string State,
    string Detail, bool IsPaused, bool CanPauseResume, bool CanReset)
{
    public string PauseResumeLabel => IsPaused ? "Resume protection" : "Pause protection";
}

internal sealed class ShellViewModel : INotifyPropertyChanged
{
    private ShellPage _page;
    private IReadOnlyList<ApplicationStatusRow> _applicationRows = [];
    private string _monitoringBanner = "Loading monitoring configuration…";
    private string _footerStatus = "Relight is in the tray · Loading monitoring status";

    public ShellViewModel(Action hide, Action exit, Action add)
    {
        ApplicationsCommand = new RelayCommand(() => Navigate(ShellPage.Applications));
        HistoryCommand = new RelayCommand(() => Navigate(ShellPage.History));
        SettingsCommand = new RelayCommand(() => Navigate(ShellPage.Settings));
        HideCommand = new RelayCommand(hide);
        ExitCommand = new RelayCommand(exit);
        AddCommand = new RelayCommand(add);
    }

    public ICommand ApplicationsCommand { get; }
    public ICommand HistoryCommand { get; }
    public ICommand SettingsCommand { get; }
    public ICommand HideCommand { get; }
    public ICommand ExitCommand { get; }
    public ICommand AddCommand { get; }
    public IReadOnlyList<ApplicationStatusRow> ApplicationRows => _applicationRows;
    public bool HasApplications => _applicationRows.Count > 0;
    public bool HasNoApplications => !HasApplications;
    public string ApplicationCountText => $"{_applicationRows.Count} configured";
    public string MonitoringBanner => _monitoringBanner;
    public string FooterStatus => _footerStatus;
    public string TrayStatus => _footerStatus.Length > 63
        ? $"Relight · {_applicationRows.Count} configured" : _footerStatus;
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

    public void UpdateMonitoring(string? configurationProblem,
        IReadOnlyList<HostedProfileStatus> profiles, EventRecorderStatus? logging)
    {
        IReadOnlyList<ApplicationStatusRow> rows = profiles
            .OrderBy(profile => profile.Name, StringComparer.CurrentCultureIgnoreCase)
            .Select(profile => new ApplicationStatusRow(profile.Id, profile.Name,
                StatusText(profile), profile.Problem ?? DetailText(profile),
                profile.Recovery?.Paused == true,
                profile.Recovery is not null && profile.AutomaticActionsAllowed &&
                    profile.Problem is null,
                profile.Recovery is { State: not Relight.Core.RecoveryState.Starting } recovery &&
                    (recovery.LockedOut || recovery.ReservedAutomaticAttempts > 0) &&
                    profile.AutomaticActionsAllowed && profile.Problem is null))
            .ToArray();
        int active = profiles.Count(profile => profile.AutomaticActionsAllowed);
        int attention = profiles.Count(profile => profile.Problem is not null);
        string banner = configurationProblem is not null
            ? $"Configuration needs attention: {configurationProblem}"
            : attention > 0
                ? $"{attention} application(s) need attention. Automatic recovery is suspended for them."
                : profiles.Count == 0
                    ? "No applications are configured. Monitoring is ready but idle."
                    : $"Monitoring {active} application(s). Closing this window keeps protection running.";
        if (logging?.Degraded == true)
            banner += " Event logging is degraded; review diagnostics before unattended use.";
        string footer = profiles.Count == 0
            ? "Relight is in the tray · No applications are configured"
            : $"Relight is in the tray · {active} protected · {attention} need attention";
        if (rows.SequenceEqual(_applicationRows) && banner == _monitoringBanner &&
            footer == _footerStatus) return;
        _applicationRows = rows;
        _monitoringBanner = banner;
        _footerStatus = footer;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(null));
    }

    public void ShowMonitoringProblem(string problem)
    {
        _applicationRows = [];
        _monitoringBanner = $"Monitoring could not start: {problem}";
        _footerStatus = "Relight is in the tray · Monitoring unavailable";
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(null));
    }

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
        profile.Recovery is { } recovery
            ? recovery.Paused
                ? $"Protection is paused; the application is left running. {recovery.ReservedAutomaticAttempts} automatic attempt(s) remain charged."
                : recovery.LockedOut
                    ? $"Automatic recovery is locked after {recovery.ReservedAutomaticAttempts} attempt(s). Reset recovery is an explicit choice."
                    : $"{recovery.ReservedAutomaticAttempts} automatic attempt(s) reserved in this episode."
            : profile.Detection?.Kind.ToString() ?? "No process result yet.";

    public void Navigate(ShellPage page)
    {
        if (_page == page) return;
        _page = page;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(null));
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}

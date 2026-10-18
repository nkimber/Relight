using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Relight.Core;
using Relight.ViewModels;
using Relight.Windows;

namespace Relight;

public partial class StorageWindow : Window
{
    private readonly Func<DiskSpaceSettings, Task> _save;
    private readonly StorageScanViewModel _scan;
    private readonly DispatcherTimer _scanAgeTimer;
    private readonly string _dataDirectory;
    private readonly SemaphoreSlim _gate;
    private readonly Action? _stopAutomatic;
    private readonly Action? _allowAutomatic;
    private readonly Action<TimeSpan>? _snooze;
    private readonly Action? _resume;
    private readonly Func<TimeSpan>? _snoozeRemaining;
    private ReviewedCleanupWindow? _cleanupWindow;
    private CleanupHistoryWindow? _historyWindow;
    public StorageWindow(DiskSpaceSettings settings, Func<DiskSpaceSettings, Task> save,
        string dataDirectory, SemaphoreSlim scanGate, Action? stopAutomatic = null, Action? allowAutomatic = null,
        Action<TimeSpan>? snooze = null, Action? resume = null, Func<TimeSpan>? snoozeRemaining = null)
    {
        InitializeComponent();
        _save = save;
        _dataDirectory = dataDirectory; _gate = scanGate;
        _stopAutomatic = stopAutomatic; _allowAutomatic = allowAutomatic;
        _snooze = snooze; _resume = resume; _snoozeRemaining = snoozeRemaining;
        _scan = new StorageScanViewModel(dataDirectory, scanGate);
        ScanPanel.DataContext = _scan;
        _scanAgeTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
        _scanAgeTimer.Tick += (_, _) => _scan.RefreshAge();
        _scanAgeTimer.Start();
        Loaded += LoadScan;
        Closed += (_, _) => { _scanAgeTimer.Stop(); _scan.Close(); };
        EnabledInput.IsChecked = settings.Enabled;
        NotificationsInput.IsChecked = settings.NotificationsEnabled;
        IntervalInput.Text = settings.CheckIntervalSeconds.ToString();
        ReminderInput.Text = settings.ReminderIntervalMinutes.ToString();
        RefreshSnooze();
        DrivesInput.ItemsSource = new ObservableCollection<DriveEditor>(
            DiskSpaceMonitor.EffectiveDrives(settings).Select(d => new DriveEditor
            {
                Root = d.Root, WarningPercent = d.WarningPercent, CriticalPercent = d.CriticalPercent,
                WarningGigabytes = d.WarningGigabytes, CriticalGigabytes = d.CriticalGigabytes
            }));
    }

    private void ReviewedCleanupClick(object sender, RoutedEventArgs e)
    {
        if (_cleanupWindow is not null) { _cleanupWindow.Activate(); return; }
        _cleanupWindow = new ReviewedCleanupWindow(_dataDirectory, _gate, _stopAutomatic, _allowAutomatic) { Owner = this };
        _cleanupWindow.Closed += (_, _) => _cleanupWindow = null;
        _cleanupWindow.Show();
    }

    private async void LoadScan(object sender, RoutedEventArgs e) => await _scan.LoadAsync();
    private void CleanupHistoryClick(object sender, RoutedEventArgs e)
    {
        if (_historyWindow is not null) { _historyWindow.Activate(); return; }
        _historyWindow = new CleanupHistoryWindow(_dataDirectory) { Owner = this };
        _historyWindow.Closed += (_, _) => _historyWindow = null;
        _historyWindow.Show();
    }
    private async void ScanClick(object sender, RoutedEventArgs e) => await _scan.ScanAsync();
    private void CancelScanClick(object sender, RoutedEventArgs e) => _scan.Cancel();
    private void BrowseScanClick(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog { Title = "Choose a folder to analyze" };
        if (dialog.ShowDialog(this) == true)
            ScanRootInput.SetCurrentValue(TextBox.TextProperty, dialog.FolderName);
    }

    public void UpdateStatus(string status) { ReadingsText.Text = status; RefreshSnooze(); }

    private void RefreshSnooze()
    {
        var remaining = _snoozeRemaining?.Invoke() ?? TimeSpan.Zero;
        SnoozeStatus.Text = remaining > TimeSpan.Zero
            ? $"Disk-space notifications snoozed for {Math.Ceiling(remaining.TotalMinutes):N0} more minute(s). A fresh low-space reading can notify when snooze ends."
            : "Disk-space notifications are not snoozed. Your saved notification preference still applies.";
        SnoozeButton.IsEnabled = _snooze is not null;
        ResumeButton.IsEnabled = _resume is not null && remaining > TimeSpan.Zero;
    }

    private void SnoozeClick(object sender, RoutedEventArgs e)
    {
        try
        {
            if (!int.TryParse(SnoozeInput.Text, out int minutes) || minutes is < 5 or > 1440)
                throw new ArgumentException("Enter 5–1440 whole minutes for snooze.");
            _snooze?.Invoke(TimeSpan.FromMinutes(minutes)); RefreshSnooze();
            SaveStatus.Text = "Disk-space notices snoozed in this instance. Monitoring and cleanup preferences are unchanged.";
        }
        catch (Exception error) { SaveStatus.Text = $"Snooze was not changed: {error.Message}"; }
    }

    private void ResumeClick(object sender, RoutedEventArgs e)
    {
        _resume?.Invoke(); RefreshSnooze();
        SaveStatus.Text = "Snooze ended. The next check evaluates current space using your saved notification preference.";
    }

    private async void SaveClick(object sender, RoutedEventArgs e)
    {
        SaveButton.IsEnabled = false;
        try
        {
            if (!DrivesInput.CommitEdit(DataGridEditingUnit.Cell, true) ||
                !DrivesInput.CommitEdit(DataGridEditingUnit.Row, true))
                throw new ArgumentException("Correct the highlighted drive values before saving.");
            if (!int.TryParse(IntervalInput.Text, out int seconds))
                throw new ArgumentException("Enter a whole number of seconds.");
            if (!int.TryParse(ReminderInput.Text, out int reminderMinutes))
                throw new ArgumentException("Enter a whole number of minutes for reminders.");
            var settings = new DiskSpaceSettings
            {
                Enabled = EnabledInput.IsChecked == true,
                NotificationsEnabled = NotificationsInput.IsChecked == true,
                CheckIntervalSeconds = seconds,
                ReminderIntervalMinutes = reminderMinutes,
                Drives = DrivesInput.ItemsSource.Cast<DriveEditor>().Select(d => new DiskSpaceDrivePolicy(
                    d.Root.Trim().ToUpperInvariant(), d.WarningPercent, d.CriticalPercent,
                    d.WarningGigabytes, d.CriticalGigabytes)).ToArray()
            };
            settings.Validate();
            await _save(settings);
            SaveStatus.Text = "Saved. New settings apply on the next check.";
        }
        catch (Exception error) { SaveStatus.Text = $"Settings were not saved: {error.Message}"; }
        finally { SaveButton.IsEnabled = true; }
    }
}

public sealed class DriveEditor
{
    public string Root { get; set; } = "";
    public double WarningPercent { get; set; } = 10;
    public double CriticalPercent { get; set; } = 5;
    public double WarningGigabytes { get; set; } = 20;
    public double CriticalGigabytes { get; set; } = 5;
}

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
    private ReviewedCleanupWindow? _cleanupWindow;
    public StorageWindow(DiskSpaceSettings settings, Func<DiskSpaceSettings, Task> save,
        string dataDirectory, SemaphoreSlim scanGate)
    {
        InitializeComponent();
        _save = save;
        _dataDirectory = dataDirectory; _gate = scanGate;
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
        _cleanupWindow = new ReviewedCleanupWindow(_dataDirectory, _gate) { Owner = this };
        _cleanupWindow.Closed += (_, _) => _cleanupWindow = null;
        _cleanupWindow.Show();
    }

    private async void LoadScan(object sender, RoutedEventArgs e) => await _scan.LoadAsync();
    private async void ScanClick(object sender, RoutedEventArgs e) => await _scan.ScanAsync();
    private void CancelScanClick(object sender, RoutedEventArgs e) => _scan.Cancel();
    private void BrowseScanClick(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog { Title = "Choose a folder to analyze" };
        if (dialog.ShowDialog(this) == true)
            ScanRootInput.SetCurrentValue(TextBox.TextProperty, dialog.FolderName);
    }

    public void UpdateStatus(string status) => ReadingsText.Text = status;

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
            var settings = new DiskSpaceSettings
            {
                Enabled = EnabledInput.IsChecked == true,
                NotificationsEnabled = NotificationsInput.IsChecked == true,
                CheckIntervalSeconds = seconds,
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

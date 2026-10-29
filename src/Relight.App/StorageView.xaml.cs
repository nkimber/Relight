using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Relight.Core;
using Relight.ViewModels;
using Relight.Windows;

namespace Relight;

public partial class StorageView : UserControl
{
    private readonly string _dataDirectory;
    private readonly SemaphoreSlim _gate;
    private readonly Func<DiskSpaceSettings> _settings;
    private readonly Func<DiskSpaceSettings, Task> _save;
    private readonly Action _stopAutomatic, _allowAutomatic, _resume;
    private readonly Action<TimeSpan> _snooze;
    private readonly Func<TimeSpan> _remaining;
    private readonly StorageScanViewModel _scan;
    private readonly DispatcherTimer _ageTimer;
    private ReviewedCleanupView? _rules;
    private CleanupHistoryView? _history;
    private StorageSettingsWindow? _settingsWindow;
    private bool _loaded, _closed, _ready;

    public StorageView(string dataDirectory, SemaphoreSlim gate, Func<DiskSpaceSettings> settings,
        Func<DiskSpaceSettings, Task> save, Action stopAutomatic, Action allowAutomatic,
        Action<TimeSpan> snooze, Action resume, Func<TimeSpan> remaining, ApplicationDiagnostics? diagnostics = null)
    {
        InitializeComponent();
        _dataDirectory = dataDirectory; _gate = gate; _settings = settings; _save = save;
        _stopAutomatic = stopAutomatic; _allowAutomatic = allowAutomatic;
        _snooze = snooze; _resume = resume; _remaining = remaining;
        _scan = new(dataDirectory, gate, () => _settings().ScanEntryLimit, diagnostics: diagnostics); UsagePanel.DataContext = _scan;
        _ageTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _ageTimer.Tick += (_, _) => _scan.RefreshAge();
        Loaded += Load;
        Unloaded += (_, _) => _ageTimer.Stop();
        _ready = true; RefreshSnooze(); RefreshScanLimit();
    }

    private async void Load(object sender, RoutedEventArgs e)
    {
        if (_closed) return;
        _ageTimer.Start();
        if (_loaded) return;
        _loaded = true; await _scan.LoadAsync(); // Saved metadata only; navigation never starts traversal.
    }

    public void ShowOverview() => Sections.SelectedIndex = 0;
    private async void OpenDiagnosticLogsClick(object sender, RoutedEventArgs e) => await ((App)Application.Current).OpenDiagnosticLogsAsync();
    private void SectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_ready || _closed || !ReferenceEquals(e.Source, Sections)) return;
        if (Sections.SelectedIndex == 2 && _rules is null)
            RulesContent.Content = _rules = new(_dataDirectory, _gate, _stopAutomatic, _allowAutomatic);
        if (Sections.SelectedIndex == 3 && _history is null)
            HistoryContent.Content = _history = new(_dataDirectory);
    }

    private void SettingsClick(object sender, RoutedEventArgs e)
    {
        if (_settingsWindow is not null) { _settingsWindow.Activate(); return; }
        try
        {
            _settingsWindow = new StorageSettingsWindow(_settings(), _save) { Owner = Window.GetWindow(this) };
            _settingsWindow.Closed += (_, _) => { _settingsWindow = null; RefreshScanLimit(); };
            _settingsWindow.Show();
        }
        catch (Exception error) { MessageBox.Show(Window.GetWindow(this), error.Message, "Storage settings unavailable"); }
    }

    public void UpdateStatus(string readings, string automatic)
    {
        ReadingsText.Text = readings; AutomaticText.Text = automatic; RefreshSnooze();
    }

    private void RefreshSnooze()
    {
        var remaining = _remaining();
        SnoozeStatus.Text = remaining > TimeSpan.Zero
            ? $"Disk-space notifications snoozed for {Math.Ceiling(remaining.TotalMinutes):N0} more minute(s). A fresh low-space reading can notify when snooze ends."
            : "Disk-space notifications are not snoozed. Your saved notification preference still applies.";
        ResumeButton.IsEnabled = remaining > TimeSpan.Zero;
    }
    private void SnoozeClick(object sender, RoutedEventArgs e)
    {
        if (!int.TryParse(SnoozeInput.Text, out int minutes) || minutes is < 5 or > 1440)
        { SaveStatus.Text = "Snooze was not changed: Enter 5–1440 whole minutes for snooze."; return; }
        _snooze(TimeSpan.FromMinutes(minutes)); RefreshSnooze();
        SaveStatus.Text = "Disk-space notices snoozed in this instance. Monitoring and cleanup preferences are unchanged.";
    }
    private void ResumeClick(object sender, RoutedEventArgs e)
    {
        _resume(); RefreshSnooze();
        SaveStatus.Text = "Snooze ended. The next check evaluates current space using your saved notification preference.";
    }
    private async void ScanClick(object sender, RoutedEventArgs e)
    {
        int version = _scan.ScanVersion;
        await _scan.ScanAsync();
        if (!_closed && IsVisible && _scan.ScanVersion != version && _scan.EntryLimitReached)
            MessageBox.Show(Window.GetWindow(this), _scan.CoverageWarning, "Scan stopped — entry limit reached",
                MessageBoxButton.OK, MessageBoxImage.Warning);
    }
    private void RefreshScanLimit()
    {
        try
        {
            int limit = _settings().ScanEntryLimit;
            ScanLimitInput.Text = limit.ToString(); ScanLimitStatus.Text = $"Saved scan limit: {limit:N0} entries.";
        }
        catch (Exception error) { ScanLimitStatus.Text = $"Scan settings unavailable: {error.Message}"; }
    }
    private async void SaveScanLimitClick(object sender, RoutedEventArgs e)
    {
        try
        {
            if (!int.TryParse(ScanLimitInput.Text, out int limit)) throw new ArgumentException("Enter a whole number of entries.");
            var settings = _settings() with { ScanEntryLimit = limit }; settings.Validate();
            await _save(settings); RefreshScanLimit();
        }
        catch (Exception error) { ScanLimitStatus.Text = $"Scan limit was not saved: {error.Message}"; }
    }
    private void CancelScanClick(object sender, RoutedEventArgs e) => _scan.Cancel();
    private void BrowseScanClick(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog { Title = "Choose a folder to analyze" };
        if (dialog.ShowDialog(Window.GetWindow(this)) == true)
            ScanRootInput.SetCurrentValue(TextBox.TextProperty, dialog.FolderName);
    }
    public void Close()
    {
        if (_closed) return;
        _closed = true; _ageTimer.Stop(); _settingsWindow?.Close();
        _scan.Close(); _rules?.Close(); _history?.Close();
    }
}

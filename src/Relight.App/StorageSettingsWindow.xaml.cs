using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using Relight.Core;
using Relight.Windows;
namespace Relight;
public partial class StorageSettingsWindow : Window
{
    private readonly Func<DiskSpaceSettings, Task> _save;
    public StorageSettingsWindow(DiskSpaceSettings settings, Func<DiskSpaceSettings, Task> save)
    {
        InitializeComponent(); _save = save;
        EnabledInput.IsChecked = settings.Enabled;
        NotificationsInput.IsChecked = settings.NotificationsEnabled;
        IntervalInput.Text = settings.CheckIntervalSeconds.ToString();
        ReminderInput.Text = settings.ReminderIntervalMinutes.ToString();
        DrivesInput.ItemsSource = new ObservableCollection<DriveEditor>(
            DiskSpaceMonitor.EffectiveDrives(settings).Select(d => new DriveEditor {
                Root=d.Root, WarningPercent=d.WarningPercent, CriticalPercent=d.CriticalPercent,
                WarningGigabytes=d.WarningGigabytes, CriticalGigabytes=d.CriticalGigabytes }));
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

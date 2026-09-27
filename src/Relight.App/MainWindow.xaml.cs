using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using Relight.Storage;
using Relight.ViewModels;

namespace Relight;

public partial class MainWindow : Window
{
    private readonly Func<Guid, bool, Task> _setPaused;
    private readonly Func<Guid, Task> _resetRecovery;
    private readonly Func<Guid, Task> _startNow;
    private readonly Func<EventHistoryQuery, string, EventHistoryExportFormat,
        Task<EventHistoryExportResult>> _exportHistory;
    private readonly Func<Guid, bool, Task> _setEnabled;
    private readonly Func<Guid, Task> _removeProfile;
    private readonly Action<Guid> _editProfile;
    private readonly Func<bool, Task> _setStartAtSignIn;
    private readonly Func<string, Task<DiagnosticBundleResult>> _exportDiagnostics;
    private bool _changingStartAtSignIn;

    public MainWindow(Func<Guid, bool, Task> setPaused, Func<Guid, Task> resetRecovery,
        Func<Guid, Task> startNow,
        Func<EventHistoryQuery, string, EventHistoryExportFormat,
            Task<EventHistoryExportResult>> exportHistory,
        Func<Guid, bool, Task> setEnabled,
        Func<Guid, Task> removeProfile,
        Action<Guid> editProfile,
        Func<bool, Task> setStartAtSignIn,
        Func<string, Task<DiagnosticBundleResult>> exportDiagnostics)
    {
        _setPaused = setPaused;
        _resetRecovery = resetRecovery;
        _startNow = startNow;
        _exportHistory = exportHistory;
        _setEnabled = setEnabled;
        _removeProfile = removeProfile;
        _editProfile = editProfile;
        _setStartAtSignIn = setStartAtSignIn;
        _exportDiagnostics = exportDiagnostics;
        InitializeComponent();
    }

    private async void StartNowClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: ApplicationStatusRow row } button) return;
        button.IsEnabled = false;
        string original = button.Content?.ToString() ?? "Start now";
        button.Content = "Checking and starting…";
        try { await _startNow(row.Id); }
        catch (OperationCanceledException) { }
        catch (Exception error)
        {
            MessageBox.Show(this, error.Message, "Start now failed",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            button.Content = original;
            button.IsEnabled = row.CanStartNow;
        }
    }

    private void OpenLogFolderClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is not ShellViewModel model) return;
        string logs = Path.Combine(model.DataDirectory, "Logs");
        if (!Directory.Exists(logs))
        {
            MessageBox.Show(this, "There are no log files yet.", "Relight history",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        try { Process.Start(new ProcessStartInfo(logs) { UseShellExecute = true }); }
        catch (Exception error)
        {
            MessageBox.Show(this, error.Message, "Could not open log folder",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private async void ExportHistoryClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button || DataContext is not ShellViewModel model) return;
        EventHistoryQuery query;
        try
        {
            DateTimeOffset now = DateTimeOffset.UtcNow;
            query = model.CreateHistoryQuery(now) with
            {
                ThroughUtc = now
            };
        }
        catch (Exception error)
        {
            MessageBox.Show(this, error.Message, "History filter is invalid",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        var picker = new SaveFileDialog
        {
            Title = "Export filtered Relight history",
            FileName = $"Relight-history-{DateTime.Now:yyyyMMdd}",
            Filter = "CSV file (*.csv)|*.csv|Text file (*.txt)|*.txt",
            DefaultExt = ".csv",
            AddExtension = true,
            OverwritePrompt = true,
            CheckPathExists = true
        };
        if (picker.ShowDialog(this) != true) return;
        EventHistoryExportFormat format = picker.FilterIndex == 2
            ? EventHistoryExportFormat.Text : EventHistoryExportFormat.Csv;
        button.IsEnabled = false;
        button.Content = "Exporting…";
        try
        {
            EventHistoryExportResult result = await _exportHistory(query,
                picker.FileName, format);
            string warning = result.SkippedMalformedLines > 0
                ? $"\n\n{result.SkippedMalformedLines} damaged log line(s) could not be exported; this export may be incomplete."
                : "";
            MessageBox.Show(this,
                $"Exported {result.ExportedEvents} matching event(s) to:\n{picker.FileName}{warning}",
                "Relight history", MessageBoxButton.OK,
                result.SkippedMalformedLines > 0 ? MessageBoxImage.Warning :
                    MessageBoxImage.Information);
        }
        catch (OperationCanceledException) { }
        catch (Exception error)
        {
            MessageBox.Show(this, error.Message, "History export failed",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            button.Content = "Export filtered history";
            button.IsEnabled = true;
        }
    }

    private async void ExportDiagnosticsClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button) return;
        var picker = new SaveFileDialog
        {
            Title = "Save local Relight diagnostics",
            FileName = $"Relight-diagnostics-{DateTime.Now:yyyyMMdd}",
            Filter = "ZIP archive (*.zip)|*.zip",
            DefaultExt = ".zip",
            AddExtension = true,
            OverwritePrompt = true,
            CheckPathExists = true
        };
        if (picker.ShowDialog(this) != true) return;
        button.IsEnabled = false;
        button.Content = "Creating bundle…";
        try
        {
            DiagnosticBundleResult result = await _exportDiagnostics(picker.FileName);
            string warning = result.SkippedMalformedLines > 0
                ? $"\n\n{result.SkippedMalformedLines} damaged log line(s) were skipped."
                : "";
            MessageBox.Show(this,
                $"Saved local diagnostics with {result.ExportedEvents} recent event(s) to:\n" +
                $"{picker.FileName}{warning}", "Relight diagnostics", MessageBoxButton.OK,
                result.SkippedMalformedLines > 0 ? MessageBoxImage.Warning :
                    MessageBoxImage.Information);
        }
        catch (OperationCanceledException) { }
        catch (Exception error)
        {
            MessageBox.Show(this, error.Message, "Diagnostic export failed",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            button.Content = "Export diagnostic bundle";
            button.IsEnabled = true;
        }
    }

    private async void PauseResumeClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: ApplicationStatusRow row } button) return;
        button.IsEnabled = false;
        try { await _setPaused(row.Id, !row.IsPaused); }
        catch (OperationCanceledException) { }
        catch (Exception error)
        {
            MessageBox.Show(this, error.Message, "Protection change failed",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally { button.IsEnabled = row.CanPauseResume; }
    }

    private async void EnableDisableClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: ApplicationStatusRow row } button) return;
        button.IsEnabled = false;
        try { await _setEnabled(row.Id, !row.ConfiguredEnabled); }
        catch (OperationCanceledException) { }
        catch (Exception error)
        {
            MessageBox.Show(this, error.Message, "Protection change failed",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally { button.IsEnabled = row.CanToggleEnabled; }
    }

    private async void RemoveProfileClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: ApplicationStatusRow row } button) return;
        if (MessageBox.Show(this,
                $"Remove '{row.Name}' from Relight? Its application will keep running. Existing history and recovery-state evidence will be retained.",
                "Remove profile?", MessageBoxButton.OKCancel,
                MessageBoxImage.Warning, MessageBoxResult.Cancel) != MessageBoxResult.OK)
            return;
        button.IsEnabled = false;
        try { await _removeProfile(row.Id); }
        catch (OperationCanceledException) { }
        catch (Exception error)
        {
            MessageBox.Show(this, error.Message, "Profile removal failed",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally { button.IsEnabled = row.CanRemove; }
    }

    private void ViewProfileHistoryClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: ApplicationStatusRow row } &&
            DataContext is ShellViewModel model)
            model.ShowHistoryFor(row.Id);
    }

    private void EditProfileClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: ApplicationStatusRow row })
            _editProfile(row.Id);
    }

    private async void StartAtSignInClick(object sender, RoutedEventArgs e)
    {
        if (_changingStartAtSignIn) return;
        _changingStartAtSignIn = true;
        StartAtSignInInput.SetCurrentValue(IsEnabledProperty, false);
        try { await _setStartAtSignIn(StartAtSignInInput.IsChecked == true); }
        catch (Exception error)
        {
            StartAtSignInInput.GetBindingExpression(
                System.Windows.Controls.Primitives.ToggleButton.IsCheckedProperty)?.UpdateTarget();
            MessageBox.Show(this, error.Message, "Could not change sign-in startup",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            _changingStartAtSignIn = false;
            StartAtSignInInput.GetBindingExpression(IsEnabledProperty)?.UpdateTarget();
        }
    }

    private async void ResetRecoveryClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: ApplicationStatusRow row } button) return;
        if (MessageBox.Show(this,
                "Reset recovery clears this episode's automatic attempt count. If the application is absent, Relight may launch it after confirming absence and waiting the normal retry delay. Continue?",
                "Reset recovery?", MessageBoxButton.OKCancel,
                MessageBoxImage.Warning, MessageBoxResult.Cancel) != MessageBoxResult.OK)
            return;
        button.IsEnabled = false;
        try { await _resetRecovery(row.Id); }
        catch (OperationCanceledException) { }
        catch (Exception error)
        {
            MessageBox.Show(this, error.Message, "Recovery reset failed",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally { button.IsEnabled = row.CanReset; }
    }
}

using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using Relight.ViewModels;

namespace Relight;

public partial class MainWindow : Window
{
    private readonly Func<Guid, bool, Task> _setPaused;
    private readonly Func<Guid, Task> _resetRecovery;
    private readonly Func<Guid, Task> _startNow;

    public MainWindow(Func<Guid, bool, Task> setPaused, Func<Guid, Task> resetRecovery,
        Func<Guid, Task> startNow)
    {
        _setPaused = setPaused;
        _resetRecovery = resetRecovery;
        _startNow = startNow;
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

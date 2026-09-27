using System;
using System.ComponentModel;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using Relight.Services;
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

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        try
        {
            _instance = new SingleInstanceService();
            if (!_instance.IsPrimary)
            {
                _instance.ActivatePrimary();
                Shutdown();
                return;
            }

            ApplyAccessibilityColors();
            SystemParameters.StaticPropertyChanged += OnSystemParametersChanged;
            _viewModel = new ShellViewModel(HideDashboard, RequestExit);
            _dashboard = new MainWindow { DataContext = _viewModel };
            MainWindow = _dashboard;
            _dashboard.Closing += OnDashboardClosing;
            _tray = new TrayService(
                () => ShowDashboard(ShellPage.Applications),
                () => ShowDashboard(ShellPage.History),
                RequestExit);
            _instance.Listen(() => Dispatcher.BeginInvoke(() => ShowDashboard()));

            if (Array.IndexOf(e.Args, "--shell-test") >= 0)
            {
                _viewModel.ShowMonitoringProblem("Shell test mode: monitoring is disabled.");
                _tray.UpdateStatus("Relight · Shell test mode");
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
            host = await RecoveryApplicationHost.OpenAsync(cancellationToken: cancellationToken)
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
        if (_updatingStatus || _exiting) return;
        RecoveryApplicationHost? host = Volatile.Read(ref _host);
        if (host is null) return;
        _updatingStatus = true;
        try
        {
            var profiles = host.GetProfiles();
            var logging = await host.GetLoggingStatusAsync();
            _viewModel?.UpdateMonitoring(host.ConfigurationProblem, profiles, logging);
            if (_viewModel is not null) _tray?.UpdateStatus(_viewModel.TrayStatus);
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
        _tray?.UpdateStatus("Relight · Monitoring unavailable");
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
        _tray?.Dispose();
        _instance?.Dispose();
        base.OnExit(e);
    }
}

using System;
using System.ComponentModel;
using System.Windows;
using Relight.Services;
using Relight.ViewModels;

namespace Relight;

public partial class App : Application
{
    private SingleInstanceService? _instance;
    private TrayService? _tray;
    private MainWindow? _dashboard;
    private ShellViewModel? _viewModel;
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
        Shutdown();
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
        SystemParameters.StaticPropertyChanged -= OnSystemParametersChanged;
        _tray?.Dispose();
        _instance?.Dispose();
        base.OnExit(e);
    }
}

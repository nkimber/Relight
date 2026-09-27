using System;
using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using Relight.Core;
using Relight.Windows;

namespace Relight;

public partial class AddApplicationWindow : Window
{
    private readonly RecoveryApplicationHost _host;
    private string? _inspectedPath;
    private bool _busy;
    private bool _saving;

    public AddApplicationWindow(RecoveryApplicationHost host)
    {
        _host = host;
        InitializeComponent();
        NameInput.Focus();
    }

    private void BrowseClick(object sender, RoutedEventArgs e)
    {
        var picker = new OpenFileDialog
        {
            Title = "Choose the application executable",
            Filter = "Applications (*.exe)|*.exe",
            CheckFileExists = true,
            Multiselect = false
        };
        if (picker.ShowDialog(this) != true) return;
        PathInput.Text = picker.FileName;
        if (string.IsNullOrWhiteSpace(NameInput.Text))
            NameInput.Text = Path.GetFileNameWithoutExtension(picker.FileName);
    }

    private void PathChanged(object sender, TextChangedEventArgs e)
    {
        _inspectedPath = null;
        if (AddButton is not null) AddButton.IsEnabled = false;
        if (DetectionText is not null)
            DetectionText.Text = "Path changed. Choose Detect now before adding protection.";
    }

    private void NameChanged(object sender, TextChangedEventArgs e)
    {
        if (AddButton is not null && PathInput is not null)
            AddButton.IsEnabled = !_busy && !string.IsNullOrWhiteSpace(NameInput.Text) &&
                _inspectedPath == PathInput.Text.Trim();
    }

    private async void DetectClick(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        SetBusy(true);
        try
        {
            string path = PathInput.Text.Trim();
            Detection result = await RecoveryApplicationHost.InspectExecutableAsync(path);
            DetectionText.Text = result.Kind switch
            {
                DetectionKind.Present => "One matching application is running in this session. It will be observed without launching a duplicate.",
                DetectionKind.Absent => "No matching application is running in this session. Relight will wait for your first launch.",
                _ => $"Identity cannot be verified: {result.Reason}"
            };
            if (result.Kind != DetectionKind.Unavailable)
                _inspectedPath = path;
        }
        catch (Exception error)
        {
            DetectionText.Text = $"Cannot inspect this executable: {error.Message}";
        }
        finally { SetBusy(false); }
    }

    private async void AddClick(object sender, RoutedEventArgs e)
    {
        if (_busy || _inspectedPath != PathInput.Text.Trim()) return;
        _saving = true;
        SetBusy(true);
        try
        {
            await _host.RegisterExecutableAsync(NameInput.Text, _inspectedPath);
            _saving = false;
            DialogResult = true;
        }
        catch (Exception error)
        {
            DetectionText.Text = $"Could not add application: {error.Message}";
        }
        finally
        {
            _saving = false;
            SetBusy(false);
        }
    }

    private void WindowClosing(object? sender, CancelEventArgs e)
    {
        if (_saving) e.Cancel = true;
    }

    private void SetBusy(bool busy)
    {
        _busy = busy;
        NameInput.IsEnabled = !busy;
        PathInput.IsEnabled = !busy;
        BrowseButton.IsEnabled = !busy;
        CancelButton.IsEnabled = !busy;
        DetectButton.IsEnabled = !busy;
        AddButton.IsEnabled = !busy && !string.IsNullOrWhiteSpace(NameInput.Text) &&
            _inspectedPath == PathInput.Text.Trim();
    }
}

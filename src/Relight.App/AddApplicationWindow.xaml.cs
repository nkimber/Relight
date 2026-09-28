using System;
using System.ComponentModel;
using System.IO;
using System.Linq;
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
    private bool _inspectedChatGpt;
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
        UseExecutablePath(picker.FileName,
            Path.GetFileNameWithoutExtension(picker.FileName));
    }

    private async void ChooseRunningClick(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        var picker = new RunningExecutableWindow { Owner = this };
        if (picker.ShowDialog() != true || picker.SelectedExecutable is not { } selected)
            return;
        UseExecutablePath(selected.ExecutablePath, selected.Name);
        await InspectSelectedAsync();
    }

    private void WindowDragOver(object sender, DragEventArgs e)
    {
        e.Effects = !_busy && DroppedExecutable(e.Data) is not null
            ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private async void WindowDrop(object sender, DragEventArgs e)
    {
        e.Handled = true;
        if (_busy) return;
        string? path = DroppedExecutable(e.Data);
        if (path is null)
        {
            DetectionText.Text = "Drop one accessible, unpackaged .exe file.";
            return;
        }
        UseExecutablePath(path, Path.GetFileNameWithoutExtension(path));
        await InspectSelectedAsync();
    }

    private void UseExecutablePath(string path, string suggestedName)
    {
        bool replaceSuggestedName = string.IsNullOrWhiteSpace(NameInput.Text) ||
            ChatGptOption.IsChecked == true && NameInput.Text == "ChatGPT";
        ExecutableOption.IsChecked = true;
        PathInput.Text = path;
        if (replaceSuggestedName) NameInput.Text = suggestedName;
    }

    private static string? DroppedExecutable(IDataObject data)
    {
        if (!data.GetDataPresent(DataFormats.FileDrop) ||
            data.GetData(DataFormats.FileDrop) is not string[] { Length: 1 } files)
            return null;
        string path = files.Single();
        try
        {
            new ExecutableTarget(path, []).Validate();
            return File.Exists(path) ? path : null;
        }
        catch (Exception error) when (error is ArgumentException or IOException or
            UnauthorizedAccessException) { return null; }
    }

    private void PathChanged(object sender, TextChangedEventArgs e)
    {
        _inspectedPath = null;
        UpdateAddState();
        if (DetectionText is not null && ChatGptOption?.IsChecked != true)
            DetectionText.Text = "Path changed. Choose Detect now before adding protection.";
    }

    private void NameChanged(object sender, TextChangedEventArgs e)
    {
        UpdateAddState();
    }

    private void TargetChanged(object sender, RoutedEventArgs e)
    {
        if (ExecutableFields is null || InstalledIdentityText is null ||
            DetectionText is null || ChatGptOption is null || NameInput is null) return;
        bool chatGpt = ChatGptOption.IsChecked == true;
        ExecutableFields.Visibility = chatGpt ? Visibility.Collapsed : Visibility.Visible;
        InstalledIdentityText.Visibility = chatGpt ? Visibility.Visible : Visibility.Collapsed;
        _inspectedPath = null;
        _inspectedChatGpt = false;
        DetectionText.Text = chatGpt
            ? "Choose Detect now to inspect the installed ChatGPT application."
            : "Select a file, then choose Detect now.";
        if (chatGpt && string.IsNullOrWhiteSpace(NameInput.Text))
            NameInput.Text = "ChatGPT";
        UpdateAddState();
    }

    private async void DetectClick(object sender, RoutedEventArgs e) =>
        await InspectSelectedAsync();

    private async System.Threading.Tasks.Task InspectSelectedAsync()
    {
        if (_busy) return;
        _inspectedPath = null;
        _inspectedChatGpt = false;
        SetBusy(true);
        try
        {
            bool chatGpt = ChatGptOption.IsChecked == true;
            string path = PathInput.Text.Trim();
            Detection result = chatGpt
                ? await RecoveryApplicationHost.InspectSelectedChatGptAsync()
                : await RecoveryApplicationHost.InspectExecutableAsync(path);
            DetectionText.Text = result.Kind switch
            {
                DetectionKind.Present => "One matching application is running in this session. It will be observed without launching a duplicate.",
                DetectionKind.Absent => "No matching application is running in this session. Relight will wait for your first launch.",
                _ => $"Identity cannot be verified: {result.Reason}"
            };
            if (result.Kind != DetectionKind.Unavailable)
            {
                if (chatGpt) _inspectedChatGpt = true;
                else _inspectedPath = path;
            }
        }
        catch (Exception error)
        {
            DetectionText.Text = $"Cannot inspect this application: {error.Message}";
        }
        finally { SetBusy(false); }
    }

    private async void AddClick(object sender, RoutedEventArgs e)
    {
        if (_busy || !CanAdd()) return;
        _saving = true;
        SetBusy(true);
        try
        {
            if (ChatGptOption.IsChecked == true)
                await _host.RegisterSelectedChatGptAsync(NameInput.Text);
            else
                await _host.RegisterExecutableAsync(NameInput.Text, _inspectedPath!,
                    ArgumentsInput.Text.Split(['\r', '\n'],
                        StringSplitOptions.RemoveEmptyEntries |
                        StringSplitOptions.TrimEntries),
                    WorkingDirectoryInput.Text);
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
        ArgumentsInput.IsEnabled = !busy;
        WorkingDirectoryInput.IsEnabled = !busy;
        BrowseButton.IsEnabled = !busy;
        ChooseRunningButton.IsEnabled = !busy;
        ExecutableOption.IsEnabled = !busy;
        ChatGptOption.IsEnabled = !busy;
        CancelButton.IsEnabled = !busy;
        DetectButton.IsEnabled = !busy;
        UpdateAddState();
    }

    private bool CanAdd() => !string.IsNullOrWhiteSpace(NameInput.Text) &&
        (ChatGptOption.IsChecked == true ? _inspectedChatGpt :
            _inspectedPath == PathInput.Text.Trim());

    private void UpdateAddState()
    {
        if (AddButton is not null && NameInput is not null && PathInput is not null &&
            ChatGptOption is not null)
            AddButton.IsEnabled = !_busy && CanAdd();
    }
}

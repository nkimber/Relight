using System;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using Relight.Core;
using Relight.Storage;
using Relight.ViewModels;

namespace Relight;

public partial class EditProfileWindow : Window
{
    private readonly ProfileConfiguration _profile;
    private readonly Func<Guid, string, TargetConfiguration, RecoveryPolicy, bool, bool, Task> _save;
    private bool _saving;

    public EditProfileWindow(ProfileConfiguration profile,
        Func<Guid, string, TargetConfiguration, RecoveryPolicy, bool, bool, Task> save)
    {
        _profile = profile;
        _save = save;
        InitializeComponent();
        NameInput.Text = profile.Name;
        IdentityInput.Text = profile.Target.Identity;
        ExecutableFields.Visibility = profile.Target.Kind == TargetKind.Executable
            ? Visibility.Visible : Visibility.Collapsed;
        PathInput.Text = profile.Target.Identity;
        ArgumentsInput.Text = string.Join(Environment.NewLine, profile.Target.Arguments);
        WorkingDirectoryInput.Text = profile.Target.WorkingDirectory ?? string.Empty;
        RecoveryPolicy policy = profile.Policy;
        NormalInput.Text = policy.NormalPollInterval.TotalSeconds.ToString(CultureInfo.CurrentCulture);
        ObservationPollInput.Text = policy.ObservationPollInterval.TotalSeconds.ToString(CultureInfo.CurrentCulture);
        LockoutInput.Text = policy.LockoutDiscoveryInterval.TotalSeconds.ToString(CultureInfo.CurrentCulture);
        ObservationInput.Text = policy.ObservationPeriod.TotalMinutes.ToString(CultureInfo.CurrentCulture);
        AttemptsInput.Text = policy.MaximumAutomaticAttempts.ToString(CultureInfo.CurrentCulture);
        RetryInput.Text = policy.RetryDelay.TotalSeconds.ToString(CultureInfo.CurrentCulture);
        AppearanceInput.Text = policy.AppearanceTimeout.TotalSeconds.ToString(CultureInfo.CurrentCulture);
        AbsenceInput.Text = policy.AbsenceConfirmationDelay.TotalSeconds.ToString(CultureInfo.CurrentCulture);
        InitialStartInput.IsChecked = policy.StartAutomaticallyWhenInitiallyAbsent;
        RearmInput.IsChecked = policy.RearmAfterStableExternalStart;
        NotifyRecoveryInput.IsChecked = profile.NotifyOnRecovery;
        NotifyLockoutInput.IsChecked = profile.NotifyOnLockout;
        UpdatePolicyPreview();
        NameInput.Focus();
    }

    private void PolicyInputChanged(object sender, TextChangedEventArgs e) =>
        UpdatePolicyPreview();

    private void PolicyToggleChanged(object sender, RoutedEventArgs e) =>
        UpdatePolicyPreview();

    private void UpdatePolicyPreview()
    {
        if (PolicyPreviewText is null || _profile is null) return;
        try { PolicyPreviewText.Text = RecoveryPolicyPreview.Describe(ReadPolicy()); }
        catch (Exception error) when (error is ArgumentException or OverflowException)
        {
            PolicyPreviewText.Text = "Enter valid monitoring and recovery values to preview this policy.";
        }
    }

    private RecoveryPolicy ReadPolicy()
    {
        RecoveryPolicy policy = _profile.Policy with
        {
            NormalPollInterval = Seconds(NormalInput.Text, "Normal check interval"),
            ObservationPollInterval = Seconds(ObservationPollInput.Text, "Observation check interval"),
            LockoutDiscoveryInterval = Seconds(LockoutInput.Text, "Lockout discovery interval"),
            ObservationPeriod = TimeSpan.FromMinutes(Number(ObservationInput.Text,
                "Stable observation period")),
            MaximumAutomaticAttempts = Number(AttemptsInput.Text, "Automatic attempt limit"),
            RetryDelay = Seconds(RetryInput.Text, "Retry delay"),
            AppearanceTimeout = Seconds(AppearanceInput.Text, "Appearance timeout"),
            AbsenceConfirmationDelay = Seconds(AbsenceInput.Text, "Absence confirmation"),
            StartAutomaticallyWhenInitiallyAbsent = InitialStartInput.IsChecked == true,
            RearmAfterStableExternalStart = RearmInput.IsChecked == true
        };
        policy.Validate();
        return policy;
    }

    private async void SaveClick(object sender, RoutedEventArgs e)
    {
        RecoveryPolicy policy;
        TargetConfiguration target;
        string name = NameInput.Text.Trim();
        try
        {
            if (name.Length is < 1 or > 100)
                throw new ArgumentException("Choose a name of 1–100 characters.");
            policy = ReadPolicy();
            target = _profile.Target.Kind == TargetKind.Executable
                ? _profile.Target with
                {
                    Identity = PathInput.Text.Trim(),
                    Arguments = [.. ArgumentsInput.Text.Split(['\r', '\n'],
                        StringSplitOptions.RemoveEmptyEntries |
                        StringSplitOptions.TrimEntries)],
                    WorkingDirectory = string.IsNullOrWhiteSpace(WorkingDirectoryInput.Text)
                        ? null : WorkingDirectoryInput.Text.Trim()
                }
                : _profile.Target;
        }
        catch (Exception error) when (error is ArgumentException or OverflowException)
        {
            MessageBox.Show(this, error.Message, "Check recovery settings",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (_profile.Enabled && policy.MaximumAutomaticAttempts > 0 &&
            policy.StartAutomaticallyWhenInitiallyAbsent &&
            (!_profile.Policy.StartAutomaticallyWhenInitiallyAbsent ||
             _profile.Policy.MaximumAutomaticAttempts == 0 ||
             !string.Equals(target.Identity, _profile.Target.Identity,
                 StringComparison.OrdinalIgnoreCase)))
        {
            MessageBoxResult choice = MessageBox.Show(this,
                "If the selected application is absent, saving this enabled profile may start it automatically after Relight confirms absence and waits the retry delay. Continue?",
                "Automatic start after Save", MessageBoxButton.YesNo,
                MessageBoxImage.Warning, MessageBoxResult.No);
            if (choice != MessageBoxResult.Yes) return;
        }

        _saving = true;
        SaveButton.IsEnabled = false;
        SaveButton.Content = "Saving…";
        try
        {
            await _save(_profile.Id, name, target, policy,
                NotifyRecoveryInput.IsChecked == true,
                NotifyLockoutInput.IsChecked == true);
            DialogResult = true;
        }
        catch (Exception error)
        {
            MessageBox.Show(this, error.Message, "Could not save profile",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            _saving = false;
            SaveButton.Content = "Save changes";
            SaveButton.IsEnabled = true;
        }
    }

    private static int Number(string text, string label) =>
        int.TryParse(text, NumberStyles.Integer, CultureInfo.CurrentCulture, out int value)
            ? value : throw new ArgumentException($"{label} must be a whole number.");

    private static TimeSpan Seconds(string text, string label) =>
        TimeSpan.FromSeconds(Number(text, label));

    private void BrowseClick(object sender, RoutedEventArgs e)
    {
        var picker = new OpenFileDialog
        {
            Title = "Choose the application executable",
            Filter = "Applications (*.exe)|*.exe",
            CheckFileExists = true,
            Multiselect = false
        };
        if (picker.ShowDialog(this) == true)
            PathInput.Text = picker.FileName;
    }

    private void WindowClosing(object? sender, CancelEventArgs e)
    {
        if (_saving) e.Cancel = true;
    }
}

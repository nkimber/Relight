using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Relight.Windows;

namespace Relight;

public partial class RunningExecutableWindow : Window
{
    private readonly RunningExecutableCatalog _catalog = new();
    private IReadOnlyList<RunningExecutableCandidate> _all = [];
    private bool _loading;

    public RunningExecutableCandidate? SelectedExecutable { get; private set; }

    public RunningExecutableWindow() => InitializeComponent();

    private async void WindowLoaded(object sender, RoutedEventArgs e)
    {
        await RefreshAsync();
        SearchInput.Focus();
    }

    private async void RefreshClick(object sender, RoutedEventArgs e) =>
        await RefreshAsync();

    private async Task RefreshAsync()
    {
        if (_loading) return;
        _loading = true;
        RefreshButton.IsEnabled = false;
        ChooseButton.IsEnabled = false;
        StatusText.Text = "Inspecting running executables…";
        try
        {
            RunningExecutableCatalogResult result = await _catalog.ListAsync();
            _all = result.Candidates;
            ApplyFilter();
            StatusText.Text = $"{result.Candidates.Count} available · {result.Skipped} unavailable or packaged. Browse for a program that is missing.";
        }
        catch (Exception error)
        {
            _all = [];
            Candidates.ItemsSource = null;
            StatusText.Text = $"Process list unavailable: {error.Message}";
        }
        finally
        {
            _loading = false;
            RefreshButton.IsEnabled = true;
            ChooseButton.IsEnabled = Candidates.SelectedItem is RunningExecutableCandidate;
        }
    }

    private void SearchChanged(object sender, TextChangedEventArgs e) => ApplyFilter();

    private void ApplyFilter()
    {
        if (Candidates is null || SearchInput is null) return;
        string filter = SearchInput.Text.Trim();
        Candidates.ItemsSource = string.IsNullOrEmpty(filter)
            ? _all
            : _all.Where(item => item.Name.Contains(filter,
                    StringComparison.CurrentCultureIgnoreCase) ||
                item.ExecutablePath.Contains(filter,
                    StringComparison.CurrentCultureIgnoreCase)).ToArray();
    }

    private void SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ChooseButton is not null)
            ChooseButton.IsEnabled = !_loading &&
                Candidates.SelectedItem is RunningExecutableCandidate;
    }

    private void CandidatesDoubleClick(object sender, MouseButtonEventArgs e) =>
        ChooseSelected();

    private void ChooseClick(object sender, RoutedEventArgs e) => ChooseSelected();

    private void ChooseSelected()
    {
        if (_loading || Candidates.SelectedItem is not RunningExecutableCandidate selected)
            return;
        SelectedExecutable = selected;
        DialogResult = true;
    }
}

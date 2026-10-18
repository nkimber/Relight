using System;
using System.Diagnostics;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using Relight.ViewModels;

namespace Relight;

public partial class ReviewedCleanupWindow : Window
{
    private readonly ReviewedCleanupViewModel _model;
    public ReviewedCleanupWindow(string dataDirectory, SemaphoreSlim gate)
    {
        InitializeComponent();
        _model = new(dataDirectory, gate); DataContext = _model;
        Loaded += Load;
        Closed += (_, _) => _model.Close();
    }
    private async void Load(object sender, RoutedEventArgs e) => await _model.LoadAsync();
    private void RuleSelected(object sender, SelectionChangedEventArgs e) => _model?.EditSelected();
    private void NewClick(object sender, RoutedEventArgs e) => _model.NewRule();
    private async void SaveClick(object sender, RoutedEventArgs e) => await _model.SaveAsync();
    private async void RemoveClick(object sender, RoutedEventArgs e) => await _model.RemoveAsync();
    private async void PreviewClick(object sender, RoutedEventArgs e) => await _model.PreviewAsync();
    private async void DeleteClick(object sender, RoutedEventArgs e)
    {
        try
        {
            CandidatesGrid.CommitEdit(DataGridEditingUnit.Cell, true);
            CandidatesGrid.CommitEdit(DataGridEditingUnit.Row, true);
            string confirmation = _model.Confirmation();
            if (MessageBox.Show(this, confirmation, "Permanently delete reviewed files?", MessageBoxButton.OKCancel,
                    MessageBoxImage.Warning, MessageBoxResult.Cancel) != MessageBoxResult.OK) return;
            await _model.DeleteAsync();
        }
        catch (Exception error) { MessageBox.Show(this, error.Message, "Cleanup not started", MessageBoxButton.OK, MessageBoxImage.Information); }
    }
    private void WindowsStorageClick(object sender, RoutedEventArgs e)
    {
        try { Process.Start(new ProcessStartInfo("ms-settings:storagesense") { UseShellExecute = true }); }
        catch (Exception error) { MessageBox.Show(this, error.Message, "Windows Storage settings unavailable"); }
    }
}

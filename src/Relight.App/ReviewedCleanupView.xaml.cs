using System;
using System.Diagnostics;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using Relight.ViewModels;

namespace Relight;

public partial class ReviewedCleanupView : UserControl
{
    private readonly ReviewedCleanupViewModel _model;
    private bool _loaded;
    public ReviewedCleanupView(string dataDirectory, SemaphoreSlim gate, Action? stopAutomatic = null, Action? allowAutomatic = null)
    {
        InitializeComponent();
        _model = new(dataDirectory, gate, stopAutomatic, allowAutomatic); DataContext = _model;
        Loaded += Load;
    }
    private async void Load(object sender, RoutedEventArgs e)
    {
        if (_loaded) return;
        _loaded = true; await _model.LoadAsync();
    }
    public void Close() => _model.Close();
    private void RuleSelected(object sender, SelectionChangedEventArgs e) => _model?.EditSelected();
    private void NewClick(object sender, RoutedEventArgs e) => _model.NewRule();
    private async void SaveClick(object sender, RoutedEventArgs e) => await _model.SaveAsync();
    private async void RemoveClick(object sender, RoutedEventArgs e) => await _model.RemoveAsync();
    private async void PreviewClick(object sender, RoutedEventArgs e) => await _model.PreviewAsync();
    private void CancelClick(object sender, RoutedEventArgs e) => _model.Cancel();
    private async void DisableAutomaticClick(object sender, RoutedEventArgs e) => await _model.DisableAutomaticAsync();
    private async void ApproveAutomaticClick(object sender, RoutedEventArgs e)
    {
        try
        {
            if (MessageBox.Show(Window.GetWindow(this), _model.AutomaticConfirmation(), "Approve automatic permanent cleanup?",
                MessageBoxButton.OKCancel, MessageBoxImage.Warning, MessageBoxResult.Cancel) != MessageBoxResult.OK) return;
            await _model.ApproveAutomaticAsync();
        }
        catch (Exception error) { MessageBox.Show(Window.GetWindow(this), error.Message, "Automatic cleanup not enabled"); }
    }
    private async void DeleteClick(object sender, RoutedEventArgs e)
    {
        try
        {
            CandidatesGrid.CommitEdit(DataGridEditingUnit.Cell, true);
            CandidatesGrid.CommitEdit(DataGridEditingUnit.Row, true);
            string confirmation = _model.Confirmation();
            if (MessageBox.Show(Window.GetWindow(this), confirmation, "Permanently delete reviewed files?", MessageBoxButton.OKCancel,
                    MessageBoxImage.Warning, MessageBoxResult.Cancel) != MessageBoxResult.OK) return;
            await _model.DeleteAsync();
        }
        catch (Exception error) { MessageBox.Show(Window.GetWindow(this), error.Message, "Cleanup not started", MessageBoxButton.OK, MessageBoxImage.Information); }
    }
    private void WindowsStorageClick(object sender, RoutedEventArgs e)
    {
        try { Process.Start(new ProcessStartInfo("ms-settings:storagesense") { UseShellExecute = true }); }
        catch (Exception error) { MessageBox.Show(Window.GetWindow(this), error.Message, "Windows Storage settings unavailable"); }
    }
}

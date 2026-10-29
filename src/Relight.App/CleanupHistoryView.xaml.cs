using System.Windows;
using System.Windows.Controls;
using Relight.ViewModels;

namespace Relight;

public partial class CleanupHistoryView : UserControl
{
    private readonly CleanupHistoryViewModel _model;
    public CleanupHistoryView(string dataDirectory)
    {
        InitializeComponent();
        _model = new(dataDirectory); DataContext = _model;
        Loaded += async (_, _) => await _model.RefreshAsync();
    }
    public void Close() => _model.Close();
    private async void RefreshClick(object sender, RoutedEventArgs e) => await _model.RefreshAsync();
    private async void RunSelected(object sender, SelectionChangedEventArgs e)
    {
        if (_model is not null) await _model.LoadSelectedAsync();
    }
}

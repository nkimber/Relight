using System;
using System.ComponentModel;
using System.IO;
using System.Windows.Input;

namespace Relight.ViewModels;

internal enum ShellPage { Applications, History, Settings }

internal sealed class ShellViewModel : INotifyPropertyChanged
{
    private ShellPage _page;

    public ShellViewModel(Action hide, Action exit)
    {
        ApplicationsCommand = new RelayCommand(() => Navigate(ShellPage.Applications));
        HistoryCommand = new RelayCommand(() => Navigate(ShellPage.History));
        SettingsCommand = new RelayCommand(() => Navigate(ShellPage.Settings));
        HideCommand = new RelayCommand(hide);
        ExitCommand = new RelayCommand(exit);
    }

    public ICommand ApplicationsCommand { get; }
    public ICommand HistoryCommand { get; }
    public ICommand SettingsCommand { get; }
    public ICommand HideCommand { get; }
    public ICommand ExitCommand { get; }
    // Selection bindings also handle radio-button arrow keys and accessibility selection,
    // which can change IsChecked without invoking a button command.
    public bool IsApplications
    {
        get => _page == ShellPage.Applications;
        set { if (value) Navigate(ShellPage.Applications); }
    }
    public bool IsHistory
    {
        get => _page == ShellPage.History;
        set { if (value) Navigate(ShellPage.History); }
    }
    public bool IsSettings
    {
        get => _page == ShellPage.Settings;
        set { if (value) Navigate(ShellPage.Settings); }
    }
    public string Heading => _page switch
    {
        ShellPage.History => "History",
        ShellPage.Settings => "Settings",
        _ => "Applications"
    };
    public string Subtitle => _page switch
    {
        ShellPage.History => "A clear account of what happened while you were away.",
        ShellPage.Settings => "A quiet presence, on your terms.",
        _ => "A home for the apps you want to keep running."
    };
    public string DataDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Relight");

    public void Navigate(ShellPage page)
    {
        if (_page == page) return;
        _page = page;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(null));
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}

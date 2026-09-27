using System;
using System.Windows.Input;

namespace Relight.ViewModels;

internal sealed class RelayCommand(Action execute) : ICommand
{
    public bool CanExecute(object? parameter) => true;
    public void Execute(object? parameter) => execute();

    // Shell navigation is always available. Stateful engine commands will own their availability.
    public event EventHandler? CanExecuteChanged { add { } remove { } }
}

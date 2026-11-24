using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;

namespace WorldEcon.Gui.ViewModels;

/// <summary>Minimal <see cref="INotifyPropertyChanged"/> base for the view-models (no MVVM framework).</summary>
public abstract class ObservableObject : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Assigns <paramref name="field"/> and raises <see cref="PropertyChanged"/> when the value changed.</summary>
    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        Raise(name!);
        return true;
    }

    protected void Raise(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>An <see cref="ICommand"/> over a delegate with an optional can-execute check.</summary>
public sealed class RelayCommand(Action execute, Func<bool>? canExecute = null) : ICommand
{
    public event EventHandler? CanExecuteChanged;

    public bool CanExecute(object? parameter) => canExecute?.Invoke() ?? true;

    public void Execute(object? parameter)
    {
        if (CanExecute(parameter)) execute();
    }

    public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}

/// <summary>One label/value line of the details panel; with <paramref name="Open"/> the value is a link that runs it.</summary>
public sealed record DetailRow(string Label, string Value, Action? Open = null)
{
    public bool HasLink => Open is not null;

    public bool HasNoLink => Open is null;
}

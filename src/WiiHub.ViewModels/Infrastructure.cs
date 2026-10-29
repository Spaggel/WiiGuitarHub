using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;

[assembly: System.Runtime.Versioning.SupportedOSPlatform("windows")]

namespace WiiHub.ViewModels;

public abstract class ObservableObject : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        Raise(name);
        return true;
    }

    protected void Raise([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public sealed class RelayCommand : ICommand
{
    private readonly Action<object?> _execute;
    private readonly Func<object?, bool>? _canExecute;

    public RelayCommand(Action execute, Func<bool>? canExecute = null)
        : this(_ => execute(), canExecute is null ? null : _ => canExecute()) { }

    public RelayCommand(Action<object?> execute, Func<object?, bool>? canExecute = null)
    {
        _execute = execute;
        _canExecute = canExecute;
    }

    public event EventHandler? CanExecuteChanged;

    public bool CanExecute(object? parameter) => _canExecute?.Invoke(parameter) ?? true;

    public void Execute(object? parameter)
    {
        if (CanExecute(parameter)) _execute(parameter);
    }

    public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}

/// <summary>Services the view layer provides to the view models.</summary>
public interface IUiServices
{
    /// <summary>Runs an action on the UI thread (asynchronously).</summary>
    void Post(Action action);

    bool Confirm(string title, string message);

    void ShowError(string title, string message);

    /// <summary>Windows notification (toast) via the tray icon.</summary>
    void ShowNotification(string title, string message, bool warning);

    void ShowPairWindow(PairViewModel viewModel);

    void ShowSettingsWindow(MainViewModel viewModel);

    void ShowLogWindow(MainViewModel viewModel);
}

/// <summary>A value plus the text shown for it in a ComboBox.</summary>
public sealed record Option<T>(T Value, string Name)
{
    public override string ToString() => Name;
}

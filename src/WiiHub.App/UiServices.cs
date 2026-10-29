using System.Windows;
using WiiHub.ViewModels;

namespace WiiHub.App;

/// <summary>WPF implementation of the services the view models need.</summary>
public sealed class UiServices : IUiServices
{
    public Window? Owner { get; set; }

    /// <summary>The notification-area icon; used for Windows notifications.</summary>
    public TrayIcon? Tray { get; set; }

    public void Post(Action action)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.HasShutdownStarted) return;
        dispatcher.BeginInvoke(action);
    }

    public bool Confirm(string title, string message)
    {
        var result = Owner is null
            ? MessageBox.Show(message, title, MessageBoxButton.YesNo, MessageBoxImage.Question)
            : MessageBox.Show(Owner, message, title, MessageBoxButton.YesNo, MessageBoxImage.Question);
        return result == MessageBoxResult.Yes;
    }

    public void ShowError(string title, string message)
    {
        if (Owner is null)
            MessageBox.Show(message, title, MessageBoxButton.OK, MessageBoxImage.Warning);
        else
            MessageBox.Show(Owner, message, title, MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    private LogWindow? _logWindow;

    public void ShowSettingsWindow(MainViewModel viewModel)
    {
        var window = new SettingsWindow { DataContext = viewModel, Owner = VisibleOwner };
        window.ShowDialog();
    }

    /// <summary>The log window is not modal and only opened once; asking again brings it forward.</summary>
    public void ShowLogWindow(MainViewModel viewModel)
    {
        if (_logWindow is { IsLoaded: true })
        {
            if (_logWindow.WindowState == WindowState.Minimized) _logWindow.WindowState = WindowState.Normal;
            _logWindow.Activate();
            return;
        }
        _logWindow = new LogWindow { DataContext = viewModel, Owner = VisibleOwner };
        _logWindow.Closed += (_, _) => _logWindow = null;
        _logWindow.Show();
    }

    /// <summary>A hidden owner (e.g. minimized to the tray) would hide its child windows too.</summary>
    private Window? VisibleOwner => Owner is { IsVisible: true } owner ? owner : null;

    public void ShowPairWindow(PairViewModel viewModel)
    {
        var window = new PairWindow { DataContext = viewModel, Owner = Owner };
        window.ShowDialog();
    }
}

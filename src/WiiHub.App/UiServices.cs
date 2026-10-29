using System.Windows;
using WiiHub.ViewModels;

namespace WiiHub.App;

/// <summary>WPF implementation of the services the view models need.</summary>
public sealed class UiServices : IUiServices
{
    public Window? Owner { get; set; }

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

    public void ShowPairWindow(PairViewModel viewModel)
    {
        var window = new PairWindow { DataContext = viewModel, Owner = Owner };
        window.ShowDialog();
    }
}

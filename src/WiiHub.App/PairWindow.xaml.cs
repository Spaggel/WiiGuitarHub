using System.ComponentModel;
using System.Windows;
using WiiHub.ViewModels;

namespace WiiHub.App;

public partial class PairWindow : Window
{
    public PairWindow()
    {
        InitializeComponent();
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        // Stop a running search; it finishes its current inquiry round in the background.
        if (DataContext is PairViewModel { IsBusy: true } vm) vm.CancelCommand.Execute(null);
    }
}

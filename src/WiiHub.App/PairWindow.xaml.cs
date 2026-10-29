using System.ComponentModel;
using System.Windows;
using WiiHub.ViewModels;

namespace WiiHub.App;

public partial class PairWindow : Window
{
    public PairWindow()
    {
        InitializeComponent();

        // Never open taller than the screen (minus a little room); the content scrolls instead.
        double maxHeight = SystemParameters.WorkArea.Height - 40;
        if (Height > maxHeight) Height = Math.Max(MinHeight, maxHeight);
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        // Stop a running search; it finishes its current inquiry round in the background.
        if (DataContext is PairViewModel { IsBusy: true } vm) vm.CancelCommand.Execute(null);
    }
}

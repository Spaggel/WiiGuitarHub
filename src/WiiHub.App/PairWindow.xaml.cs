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

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        // Start searching right away: the user only has to press SYNC on the Wiimote.
        (DataContext as PairViewModel)?.StartAutoScan();
    }

    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        // Stop searching; the current inquiry round finishes in the background.
        (DataContext as PairViewModel)?.Stop();
    }
}

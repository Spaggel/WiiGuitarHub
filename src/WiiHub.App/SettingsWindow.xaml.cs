using System.Windows;

namespace WiiHub.App;

public partial class SettingsWindow : Window
{
    public SettingsWindow()
    {
        InitializeComponent();
        double maxHeight = SystemParameters.WorkArea.Height - 40;
        if (Height > maxHeight) Height = Math.Max(MinHeight, maxHeight);
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}

using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Navigation;
using WiiHub.ViewModels;

namespace WiiHub.App;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
    }

    private MainViewModel? ViewModel => DataContext as MainViewModel;

    /// <summary>"Press key" in the mapping editor: the next key press becomes the binding's target.</summary>
    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        var vm = ViewModel;
        if (vm is null) return;

        // F2 renames the selected controller (like renaming a file in Explorer).
        if (e.Key == Key.F2 && !vm.IsCapturingKey && vm.Selected is { IsRenaming: false } selected)
        {
            selected.StartRename();
            e.Handled = true;
            return;
        }

        if (!vm.IsCapturingKey) return;

        Key key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key == Key.Escape)
        {
            vm.CancelKeyCapture();
        }
        else
        {
            int vk = KeyInterop.VirtualKeyFromKey(key);
            if (vk != 0) vm.CompleteKeyCapture(vk);
        }
        e.Handled = true;
    }

    // ------------------------------------------------------------------ renaming

    /// <summary>When the rename box appears, put the cursor in it with the old name selected.</summary>
    private void RenameBox_IsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.NewValue is not true) return;
        Dispatcher.InvokeAsync(() =>
        {
            RenameBox.Focus();
            Keyboard.Focus(RenameBox);
            RenameBox.SelectAll();
        }, System.Windows.Threading.DispatcherPriority.Input);
    }

    /// <summary>Clicking somewhere else saves the name, like in Explorer.</summary>
    private void RenameBox_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (RenameBox.DataContext is ControllerViewModel { IsRenaming: true } c) c.CommitRename();
    }

    private void ControllerList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        // Only when the double-click landed on a controller, not on empty list space.
        if (e.OriginalSource is DependencyObject source &&
            ItemsControl.ContainerFromElement((ItemsControl)sender, source) is ListBoxItem { DataContext: ControllerViewModel c })
        {
            c.StartRename();
        }
    }

    private void RenameMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: ControllerViewModel c } && ViewModel is { } vm)
        {
            vm.Selected = c;
            c.StartRename();
        }
    }

    private void Hyperlink_RequestNavigate(object sender, RequestNavigateEventArgs e)
    {
        Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true });
        e.Handled = true;
    }
}

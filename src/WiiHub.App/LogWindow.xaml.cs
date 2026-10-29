using System.Collections.Specialized;
using System.Windows;
using WiiHub.ViewModels;

namespace WiiHub.App;

/// <summary>The log, reachable at any time (also before any controller exists).</summary>
public partial class LogWindow : Window
{
    public LogWindow()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        Loaded += (_, _) => ScrollToEnd();
        Closed += (_, _) =>
        {
            if (DataContext is MainViewModel vm) vm.LogLines.CollectionChanged -= OnLogChanged;
        };
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.OldValue is MainViewModel oldVm) oldVm.LogLines.CollectionChanged -= OnLogChanged;
        if (e.NewValue is MainViewModel newVm) newVm.LogLines.CollectionChanged += OnLogChanged;
    }

    private void OnLogChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action == NotifyCollectionChangedAction.Add) ScrollToEnd();
    }

    private void ScrollToEnd()
    {
        if (LogList.Items.Count > 0) LogList.ScrollIntoView(LogList.Items[LogList.Items.Count - 1]);
    }

    private void CopyAll_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel vm) return;
        try
        {
            Clipboard.SetText(string.Join(Environment.NewLine, vm.LogLines));
        }
        catch (System.Runtime.InteropServices.ExternalException)
        {
            // Clipboard is briefly locked by another program; the user can simply try again.
        }
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}

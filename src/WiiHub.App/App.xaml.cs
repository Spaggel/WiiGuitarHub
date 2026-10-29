using System.Windows;
using System.Windows.Threading;
using WiiHub.App.Output;
using WiiHub.Core;
using WiiHub.Core.Config;
using WiiHub.Core.Controllers;
using WiiHub.ViewModels;

namespace WiiHub.App;

public partial class App : Application
{
    private ControllerHub? _hub;
    private ViGEmPadFactory? _padFactory;
    private MainViewModel? _viewModel;
    private DispatcherTimer? _uiTimer;

    // Only one copy may run: two copies would fight over the same Wiimotes.
    private const string InstanceMutexName = @"Local\WiiGuitarHub.Instance";
    private const string ActivateEventName = @"Local\WiiGuitarHub.Activate";
    private Mutex? _instanceMutex;
    private EventWaitHandle? _activateEvent;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        if (!ClaimSingleInstance())
        {
            // Already running (e.g. started with Windows): bring that window to the front instead.
            Shutdown();
            return;
        }

        DispatcherUnhandledException += OnUnhandledException;
        bool startMinimized = e.Args.Any(a => a.Equals(Autostart.MinimizedArg, StringComparison.OrdinalIgnoreCase));
        Autostart.RepairPath();

        var store = new ConfigStore();
        AppConfig config = store.Load();
        Log.IncludeDebug = config.ShowDebugLog;
        Log.Info($"Settings: {store.Path}");

        _padFactory = new ViGEmPadFactory();
        _hub = new ControllerHub(config, store, _padFactory);

        var ui = new UiServices();
        _viewModel = new MainViewModel(_hub, ui);

        var window = new MainWindow { DataContext = _viewModel };
        ui.Owner = window;
        MainWindow = window;
        if (startMinimized) window.WindowState = WindowState.Minimized;
        window.Show();

        _hub.Start();

        // The live input view refreshes at ~30 fps; the controllers themselves run at ~100 Hz
        // on their own threads, independent of the UI.
        _uiTimer = new DispatcherTimer(DispatcherPriority.Render)
        {
            Interval = TimeSpan.FromMilliseconds(33),
        };
        _uiTimer.Tick += (_, _) => _viewModel.Tick();
        _uiTimer.Start();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _uiTimer?.Stop();
        _viewModel?.Dispose();
        _hub?.Dispose();
        _padFactory?.Dispose();
        _activateEvent?.Dispose();
        _instanceMutex?.Dispose();
        base.OnExit(e);
    }

    /// <summary>
    /// Returns false when another copy is already running; that copy is asked to show its window.
    /// </summary>
    private bool ClaimSingleInstance()
    {
        _instanceMutex = new Mutex(initiallyOwned: true, InstanceMutexName, out bool createdNew);
        if (!createdNew)
        {
            try
            {
                using var activate = EventWaitHandle.OpenExisting(ActivateEventName);
                activate.Set();
            }
            catch (WaitHandleCannotBeOpenedException) { /* the other copy is still starting or closing */ }
            _instanceMutex.Dispose();
            _instanceMutex = null;
            return false;
        }

        var activateEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ActivateEventName);
        _activateEvent = activateEvent;
        var listener = new Thread(() =>
        {
            try
            {
                while (activateEvent.WaitOne())
                    Dispatcher.InvokeAsync(BringToFront);
            }
            catch (ObjectDisposedException) { /* shutting down */ }
        })
        {
            IsBackground = true,
            Name = "Single-instance listener",
        };
        listener.Start();
        return true;
    }

    private void BringToFront()
    {
        if (MainWindow is not { } window) return;
        if (window.WindowState == WindowState.Minimized) window.WindowState = WindowState.Normal;
        window.Show();
        window.Activate();
        // Windows only lets a background app steal focus briefly; topmost toggling makes it visible.
        window.Topmost = true;
        window.Topmost = false;
        window.Focus();
    }

    private void OnUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Log.Error($"Unexpected error: {e.Exception}");
        // MessageBox.Show(Window, …) throws ArgumentNullException for a null owner, which is the
        // case when OnStartup fails before the main window exists.
        var owner = MainWindow;
        if (owner is not null)
            MessageBox.Show(owner, e.Exception.Message, "WiiHub — unexpected error",
                MessageBoxButton.OK, MessageBoxImage.Error);
        else
            MessageBox.Show(e.Exception.Message, "WiiHub — unexpected error",
                MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;

        // Startup failed before the main window was shown: without this the process would keep
        // running invisibly (ShutdownMode is OnMainWindowClose and no window will ever close).
        if (owner is null || !owner.IsVisible) Shutdown(1);
    }
}

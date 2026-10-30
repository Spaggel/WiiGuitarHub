using System.Collections.ObjectModel;
using WiiHub.Core;
using WiiHub.Core.Bluetooth;
using WiiHub.Core.Config;
using WiiHub.Core.Controllers;

namespace WiiHub.ViewModels;

public sealed class MainViewModel : ObservableObject, IDisposable
{
    private const int MaxLogLines = 400;

    private ControllerViewModel? _selected;
    private BindingViewModel? _keyCaptureTarget;
    private string _xboxStatus = "";
    private bool _xboxAvailable;
    private string _bluetoothStatus = "Checking Bluetooth…";
    private bool _bluetoothAvailable = true;

    public MainViewModel(ControllerHub hub, IUiServices ui)
    {
        Hub = hub;
        Ui = ui;

        foreach (var slot in hub.Slots) AddSlot(slot);
        Selected = Controllers.FirstOrDefault();

        hub.SlotAdded += slot => ui.Post(() =>
        {
            var vm = AddSlot(slot);
            Selected ??= vm;
        });
        hub.ControllerConnected += slot => ui.Post(() => NotifyConnection(slot, connected: true, TurnOffReason.None));
        hub.ControllerDisconnected += (slot, why) => ui.Post(() => NotifyConnection(slot, connected: false, why));
        hub.LowBattery += (slot, percent) =>
        {
            if (!Hub.Config.LowBatteryNotify) return;
            ui.Post(() => Ui.ShowNotification(
                $"{slot.Config.Name}: battery low",
                $"Battery at {percent}%. Replace the Wiimote's batteries soon.",
                warning: true));
        };
        hub.SlotRemoved += slot => ui.Post(() =>
        {
            var vm = Controllers.FirstOrDefault(c => c.Slot == slot);
            if (vm is null) return;
            Controllers.Remove(vm);
            if (Selected == vm) Selected = Controllers.FirstOrDefault();
            Raise(nameof(HasNoControllers));
        });

        foreach (var e in Log.Snapshot()) LogLines.Add(e.ToString());
        Log.EntryAdded += OnLog;

        PairCommand = new RelayCommand(() => Ui.ShowPairWindow(new PairViewModel(Ui)));
        OpenSettingsCommand = new RelayCommand(() => Ui.ShowSettingsWindow(this));
        OpenLogCommand = new RelayCommand(() => Ui.ShowLogWindow(this));
        TurnOffAllCommand = new RelayCommand(TurnOffAll, () => _anyConnected && !_turningOffAll);
        ClearLogCommand = new RelayCommand(LogLines.Clear);

        if (hub.PadFactory is { } factory)
        {
            XboxAvailable = factory.IsAvailable(out var msg);
            XboxStatus = msg;
        }
        else
        {
            XboxAvailable = false;
            XboxStatus = "ViGEmBus driver not found — Xbox 360 output disabled. Keyboard output still works.";
        }

        var autostart = Autostart.Read();
        _startWithWindows = autostart.Enabled;
        _startMinimized = !autostart.Enabled || autostart.Minimized; // default: minimized

        CheckForUpdatesNowCommand = new RelayCommand(() => _ = CheckForUpdatesAsync(userAsked: true), () => !_checkingForUpdates);
        if (Hub.Config.CheckForUpdates) _ = CheckForUpdatesAsync(userAsked: false);

        Task.Run(() =>
        {
            bool ok = WiimoteBluetooth.IsBluetoothAvailable(out string status);
            Ui.Post(() =>
            {
                BluetoothAvailable = ok;
                BluetoothStatus = status;
            });
        });
    }

    public ControllerHub Hub { get; }
    public IUiServices Ui { get; }

    public ObservableCollection<ControllerViewModel> Controllers { get; } = [];
    public ObservableCollection<string> LogLines { get; } = [];

    public RelayCommand PairCommand { get; }
    public RelayCommand OpenSettingsCommand { get; }
    public RelayCommand OpenLogCommand { get; }
    public RelayCommand TurnOffAllCommand { get; }

    // ------------------------------------------------------------------ turning off

    private bool _anyConnected;
    private bool _turningOffAll;

    private void TurnOffAll()
    {
        _turningOffAll = true;
        TurnOffAllCommand.RaiseCanExecuteChanged();
        Task.Run(() =>
        {
            int failed = Hub.TurnOffAll(out string? error);
            Ui.Post(() =>
            {
                _turningOffAll = false;
                TurnOffAllCommand.RaiseCanExecuteChanged();
                if (failed > 0 && error is not null) Ui.ShowError("Couldn't turn off every Wiimote", error);
            });
        });
    }

    private static readonly IReadOnlyList<Option<int>> s_autoOffOptions =
    [
        new(0, "Never"),
        new(5, "After 5 minutes"),
        new(10, "After 10 minutes"),
        new(15, "After 15 minutes"),
        new(30, "After 30 minutes"),
        new(60, "After 1 hour"),
    ];

    public IReadOnlyList<Option<int>> AutoOffOptions => s_autoOffOptions;

    /// <summary>Idle Wiimotes are turned off after this many minutes (0 = never).</summary>
    public int AutoOffMinutes
    {
        get => Hub.Config.AutoOffMinutes;
        set
        {
            if (Hub.Config.AutoOffMinutes == value) return;
            Hub.Config.AutoOffMinutes = Math.Max(0, value);
            Raise();
            Hub.RequestSave();
        }
    }

    // ------------------------------------------------------------------ tray

    public bool MinimizeToTray
    {
        get => Hub.Config.MinimizeToTray;
        set
        {
            if (Hub.Config.MinimizeToTray == value) return;
            Hub.Config.MinimizeToTray = value;
            Raise();
            Hub.RequestSave();
        }
    }

    public bool CloseToTray
    {
        get => Hub.Config.CloseToTray;
        set
        {
            if (Hub.Config.CloseToTray == value) return;
            Hub.Config.CloseToTray = value;
            Raise();
            Hub.RequestSave();
        }
    }

    /// <summary>One-line summary for the tray tooltip, e.g. "2 of 3 controllers connected".</summary>
    public string TraySummary
    {
        get
        {
            int total = Controllers.Count, connected = Controllers.Count(c => c.IsConnected);
            return total == 0 ? "No controllers paired" : $"{connected} of {total} connected";
        }
    }

    // ------------------------------------------------------------------ updates

    private bool _checkingForUpdates;
    private bool _updateAvailable;
    private string _updateVersion = "";
    private string _updateUrl = UpdateChecker.ReleasesPage;
    private string _updateStatus = "";

    public string VersionText => $"Version {UpdateChecker.CurrentVersionText}";

    public bool CheckForUpdates
    {
        get => Hub.Config.CheckForUpdates;
        set
        {
            if (Hub.Config.CheckForUpdates == value) return;
            Hub.Config.CheckForUpdates = value;
            Raise();
            Hub.RequestSave();
        }
    }

    public bool UpdateAvailable { get => _updateAvailable; private set => Set(ref _updateAvailable, value); }
    public string UpdateVersion { get => _updateVersion; private set => Set(ref _updateVersion, value); }
    public string UpdateUrl { get => _updateUrl; private set => Set(ref _updateUrl, value); }

    /// <summary>Result of the last check, shown in Settings.</summary>
    public string UpdateStatus { get => _updateStatus; private set => Set(ref _updateStatus, value); }

    public RelayCommand CheckForUpdatesNowCommand { get; }

    private async Task CheckForUpdatesAsync(bool userAsked)
    {
        Ui.Post(() =>
        {
            _checkingForUpdates = true;
            UpdateStatus = "Checking…";
            CheckForUpdatesNowCommand.RaiseCanExecuteChanged();
        });

        var result = await UpdateChecker.CheckAsync().ConfigureAwait(false);
        if (result.Status == WiiHub.Core.Config.UpdateStatus.Failed) Log.Warn(result.Message);
        else Log.Info(result.Message);

        Ui.Post(() =>
        {
            _checkingForUpdates = false;
            CheckForUpdatesNowCommand.RaiseCanExecuteChanged();
            UpdateStatus = result.Message;
            if (result.Update is { } update)
            {
                UpdateVersion = UpdateChecker.Format(update.Version);
                UpdateUrl = update.Url;
                UpdateAvailable = true;
                if (!userAsked)
                    Ui.ShowNotification("Wii Guitar Hub update available",
                        $"Version {UpdateVersion} is available. Open Wii Guitar Hub to download it.", warning: false);
            }
        });
    }

    // ------------------------------------------------------------------ theme

    private static readonly IReadOnlyList<Option<AppTheme>> s_themeOptions =
    [
        new(AppTheme.System, "Follow Windows"),
        new(AppTheme.Light, "Light"),
        new(AppTheme.Dark, "Dark"),
    ];

    public IReadOnlyList<Option<AppTheme>> ThemeOptions => s_themeOptions;

    public AppTheme Theme
    {
        get => Hub.Config.Theme;
        set
        {
            if (Hub.Config.Theme == value) return;
            Hub.Config.Theme = value;
            Raise();
            Hub.RequestSave();
            Ui.ApplyTheme(value);
        }
    }

    // ------------------------------------------------------------------ connection notifications

    /// <summary>Per controller: when its last connect/disconnect notification was shown.</summary>
    private readonly Dictionary<string, long> _lastConnectionToast = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>A flaky connection can drop and come back quickly; show at most one toast per controller in this time.</summary>
    private const long ConnectionToastCooldownMs = 10_000;

    public bool ConnectionNotifications
    {
        get => Hub.Config.ConnectionNotifications;
        set
        {
            if (Hub.Config.ConnectionNotifications == value) return;
            Hub.Config.ConnectionNotifications = value;
            Raise();
            Hub.RequestSave();
        }
    }

    private void NotifyConnection(ControllerSlot slot, bool connected, TurnOffReason why)
    {
        if (!Hub.Config.ConnectionNotifications) return;
        // The user turned it off themselves (Turn off / Turn off all): no need to tell them.
        if (!connected && why == TurnOffReason.Manual) return;

        long now = Environment.TickCount64;
        bool important = !connected && why == TurnOffReason.None; // lost mid-game: always worth showing
        if (!important && _lastConnectionToast.TryGetValue(slot.Id, out long last) && now - last < ConnectionToastCooldownMs)
            return;
        _lastConnectionToast[slot.Id] = now;

        string name = Controllers.FirstOrDefault(c => c.Slot == slot)?.Name ?? slot.Config.Name;
        var (title, message) = (connected, why) switch
        {
            (true, _) => ($"{name} connected", $"Connected as Player {slot.Config.PlayerIndex}."),
            (false, TurnOffReason.Idle) => ($"{name} turned off",
                $"Not used for {Hub.Config.AutoOffMinutes} minutes. Press any button on the Wiimote to turn it on again."),
            _ => ($"{name} disconnected", "Press any button on the Wiimote to reconnect."),
        };
        Ui.ShowNotification(title, message, warning: !connected && why == TurnOffReason.None);
    }

    // ------------------------------------------------------------------ low battery

    public bool LowBatteryNotify
    {
        get => Hub.Config.LowBatteryNotify;
        set
        {
            if (Hub.Config.LowBatteryNotify == value) return;
            Hub.Config.LowBatteryNotify = value;
            Raise();
            Hub.RequestSave();
        }
    }

    public bool LowBatteryRumble
    {
        get => Hub.Config.LowBatteryRumble;
        set
        {
            if (Hub.Config.LowBatteryRumble == value) return;
            Hub.Config.LowBatteryRumble = value;
            Raise();
            Hub.RequestSave();
        }
    }

    public string LowBatteryText => $"When a Wiimote's battery drops to {AppConfig.LowBatteryPercent}% or below:";

    // ------------------------------------------------------------------ start with Windows

    private bool _startWithWindows;
    private bool _startMinimized;

    /// <summary>Start the app when the user logs in (handy for dedicated Clone Hero / YARG PCs).</summary>
    public bool StartWithWindows
    {
        get => _startWithWindows;
        set
        {
            if (_startWithWindows == value) return;
            if (!ApplyAutostart(value, _startMinimized)) return;
            _startWithWindows = value;
            Raise();
        }
    }

    /// <summary>When started with Windows, start with the window minimised.</summary>
    public bool StartMinimized
    {
        get => _startMinimized;
        set
        {
            if (_startMinimized == value) return;
            if (_startWithWindows && !ApplyAutostart(true, value)) return;
            _startMinimized = value;
            Raise();
        }
    }

    private bool ApplyAutostart(bool enabled, bool minimized)
    {
        try
        {
            Autostart.Set(enabled, minimized);
            return true;
        }
        catch (Exception ex)
        {
            Log.Error($"Could not change the startup setting: {ex.Message}");
            Ui.ShowError("Start with Windows", $"Could not change the startup setting: {ex.Message}");
            // The checkbox already flipped in the UI; tell it to flip back.
            Raise(nameof(StartWithWindows));
            Raise(nameof(StartMinimized));
            return false;
        }
    }
    public RelayCommand ClearLogCommand { get; }

    public ControllerViewModel? Selected
    {
        get => _selected;
        set
        {
            if (ReferenceEquals(_selected, value)) return;
            _selected?.CommitRename(); // keep a half-typed name when switching controllers
            if (!Set(ref _selected, value)) return;
            CancelKeyCapture();
            Raise(nameof(HasSelection));
            Raise(nameof(HasNoControllers));
        }
    }

    public bool HasSelection => _selected is not null;
    public bool HasNoControllers => Controllers.Count == 0;

    public string XboxStatus { get => _xboxStatus; private set => Set(ref _xboxStatus, value); }
    public bool XboxAvailable { get => _xboxAvailable; private set => Set(ref _xboxAvailable, value); }
    public string BluetoothStatus { get => _bluetoothStatus; private set => Set(ref _bluetoothStatus, value); }
    public bool BluetoothAvailable { get => _bluetoothAvailable; private set => Set(ref _bluetoothAvailable, value); }

    /// <summary>Call from a UI timer at ~30 Hz.</summary>
    public void Tick()
    {
        bool any = false;
        foreach (var c in Controllers)
        {
            c.UpdateStatus();
            c.Tick(c == _selected);
            any |= c.IsConnected;
        }
        if (any != _anyConnected)
        {
            _anyConnected = any;
            TurnOffAllCommand.RaiseCanExecuteChanged();
        }
    }

    // ------------------------------------------------------------------ key capture

    /// <summary>True while the mapping editor waits for a keyboard key.</summary>
    public bool IsCapturingKey => _keyCaptureTarget is not null;

    public void BeginKeyCapture(BindingViewModel binding)
    {
        CancelKeyCapture();
        _keyCaptureTarget = binding;
        binding.IsCapturingKey = true;
        Raise(nameof(IsCapturingKey));
    }

    /// <summary>The view calls this with the virtual-key code of the pressed key.</summary>
    public void CompleteKeyCapture(int virtualKey)
    {
        var target = _keyCaptureTarget;
        if (target is null) return;
        CancelKeyCapture();
        _selected?.EnsureTargetOption(virtualKey);
        target.Target = virtualKey;
    }

    public void CancelKeyCapture()
    {
        if (_keyCaptureTarget is null) return;
        _keyCaptureTarget.IsCapturingKey = false;
        _keyCaptureTarget = null;
        Raise(nameof(IsCapturingKey));
    }

    // ------------------------------------------------------------------ helpers

    private ControllerViewModel AddSlot(ControllerSlot slot)
    {
        var vm = new ControllerViewModel(this, slot);
        Controllers.Add(vm);
        Raise(nameof(HasNoControllers));
        return vm;
    }

    private void OnLog(LogEntry e) => Ui.Post(() =>
    {
        LogLines.Add(e.ToString());
        while (LogLines.Count > MaxLogLines) LogLines.RemoveAt(0);
    });

    public void Dispose()
    {
        Log.EntryAdded -= OnLog;
    }
}

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

using System.Collections.ObjectModel;
using WiiHub.Core;
using WiiHub.Core.Bluetooth;

namespace WiiHub.ViewModels;

public sealed class KnownWiimoteViewModel(BluetoothWiimoteInfo info)
{
    public BluetoothWiimoteInfo Info { get; } = info;
    public string Name => Info.Name;
    public string Address => Info.AddressText;
    public string State => Info.Connected ? "Connected" : Info.Authenticated ? "Paired" : "Remembered";
}

/// <summary>The "Pair Wiimote" window.</summary>
public sealed class PairViewModel : ObservableObject
{
    private readonly IUiServices _ui;
    private CancellationTokenSource? _cts;
    private bool _isBusy;
    private PairMode _mode = PairMode.Permanent;
    private string _bluetoothStatus = "Checking Bluetooth…";

    public PairViewModel(IUiServices ui)
    {
        _ui = ui;
        StartCommand = new RelayCommand(Start, () => !IsBusy);
        CancelCommand = new RelayCommand(Cancel, () => IsBusy);
        // Both work while searching: removing an old pairing is often exactly what lets a SYNC'd Wiimote pair.
        RefreshCommand = new RelayCommand(RefreshKnown);
        RemoveCommand = new RelayCommand(p => { if (p is KnownWiimoteViewModel k) RemoveKnown(k); });
        RefreshKnown();
    }

    public ObservableCollection<string> Messages { get; } = [];
    public ObservableCollection<KnownWiimoteViewModel> KnownWiimotes { get; } = [];

    public RelayCommand StartCommand { get; }
    public RelayCommand CancelCommand { get; }
    public RelayCommand RefreshCommand { get; }
    public RelayCommand RemoveCommand { get; }

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (!Set(ref _isBusy, value)) return;
            StartCommand.RaiseCanExecuteChanged();
            CancelCommand.RaiseCanExecuteChanged();
        }
    }

    /// <summary>Stop searching when nothing new was paired for this long (inquiries keep the radio busy).</summary>
    public static readonly TimeSpan StopWhenIdleFor = TimeSpan.FromMinutes(3);

    private string _scanStatus = "";
    /// <summary>One live line about the search (not added to the message list).</summary>
    public string ScanStatus { get => _scanStatus; private set => Set(ref _scanStatus, value); }

    private bool _restartAfterStop;

    /// <summary>Called when the window opens: start searching right away.</summary>
    public void StartAutoScan()
    {
        if (!IsBusy) Start();
    }

    /// <summary>Called when the window closes.</summary>
    public void Stop()
    {
        _restartAfterStop = false;
        _cts?.Cancel();
    }

    public bool PermanentMode
    {
        get => _mode == PairMode.Permanent;
        set
        {
            if (value) Mode = PairMode.Permanent;
        }
    }

    public bool TemporaryMode
    {
        get => _mode == PairMode.Temporary;
        set
        {
            if (value) Mode = PairMode.Temporary;
        }
    }

    private PairMode Mode
    {
        get => _mode;
        set
        {
            if (!Set(ref _mode, value)) return;
            Raise(nameof(PermanentMode));
            Raise(nameof(TemporaryMode));
            Raise(nameof(Instructions));

            // The PIN depends on the method, so restart the search with the new one.
            if (IsBusy)
            {
                _restartAfterStop = true;
                _cts?.Cancel();
                ScanStatus = "Switching pairing method…";
            }
        }
    }

    public string Instructions => _mode == PairMode.Permanent
        ? "Open the battery cover and press the small red SYNC button. The LEDs start blinking and the " +
          "Wiimote is paired automatically within a few seconds. Paired this way, it reconnects by itself " +
          "whenever you press a button — also after swapping batteries. You can pair several Wiimotes one " +
          "after another."
        : "Hold 1 and 2 until the LEDs blink; the Wiimote is paired automatically within a few seconds. " +
          "It forgets the PC when it turns off, so you will need to pair again next time.";

    public string BluetoothStatus
    {
        get => _bluetoothStatus;
        private set => Set(ref _bluetoothStatus, value);
    }

    private void Start()
    {
        IsBusy = true;
        _restartAfterStop = false;
        var cts = new CancellationTokenSource();
        _cts = cts;
        var mode = _mode;
        ScanStatus = mode == PairMode.Permanent
            ? "Searching… press the red SYNC button on the Wiimote."
            : "Searching… hold 1 + 2 on the Wiimote.";

        Task.Run(() =>
        {
            var end = WiimoteBluetooth.ScanEnd.Cancelled;
            try
            {
                end = WiimoteBluetooth.ScanAndPair(mode, Report, OnPaired, StopWhenIdleFor, cts.Token);
            }
            catch (Exception ex)
            {
                Report($"Searching failed: {ex.Message}");
                Log.Error($"Searching for Wiimotes failed: {ex}");
            }
            finally
            {
                _ui.Post(() =>
                {
                    cts.Dispose();
                    if (ReferenceEquals(_cts, cts)) _cts = null;
                    IsBusy = false;
                    if (_restartAfterStop)
                    {
                        Start();
                        return;
                    }
                    ScanStatus = end switch
                    {
                        WiimoteBluetooth.ScanEnd.Idle =>
                            $"Stopped searching after {StopWhenIdleFor.TotalMinutes:0} minutes without a new Wiimote. Click “Search again” to continue.",
                        WiimoteBluetooth.ScanEnd.NoAdapter => "No Bluetooth adapter found.",
                        _ => "Search stopped.",
                    };
                    RefreshKnown();
                });
            }
        });
    }

    private void OnPaired(PairResult result)
    {
        _ui.Post(() =>
        {
            Messages.Add($"✔ {result.Name} ({BtAddress.Format(result.Address)}) paired. It shows up in the main window within a few seconds.");
            RefreshKnown();
        });
    }

    private void Cancel()
    {
        _restartAfterStop = false;
        _cts?.Cancel();
        ScanStatus = "Stopping after the current search round…";
    }

    private void Report(string message)
    {
        Log.Info(message);
        _ui.Post(() => Messages.Add(message));
    }

    private void RefreshKnown()
    {
        Task.Run(() =>
        {
            bool ok = WiimoteBluetooth.IsBluetoothAvailable(out string status);
            var known = ok ? SafeGetKnown() : [];
            _ui.Post(() =>
            {
                BluetoothStatus = status;
                KnownWiimotes.Clear();
                foreach (var k in known) KnownWiimotes.Add(new KnownWiimoteViewModel(k));
            });
        });
    }

    private static List<BluetoothWiimoteInfo> SafeGetKnown()
    {
        try { return WiimoteBluetooth.GetKnownWiimotes(); }
        catch (Exception ex)
        {
            Log.Error($"Listing paired Wiimotes failed: {ex.Message}");
            return [];
        }
    }

    private void RemoveKnown(KnownWiimoteViewModel k)
    {
        if (!_ui.Confirm("Remove pairing",
                $"Remove {k.Address} from Windows? You will need to pair it again (SYNC button).")) return;
        Task.Run(() =>
        {
            WiimoteBluetooth.Remove(k.Info.Address, Report);
            _ui.Post(RefreshKnown);
        });
    }
}

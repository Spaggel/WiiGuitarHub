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
        RefreshCommand = new RelayCommand(RefreshKnown, () => !IsBusy);
        RemoveCommand = new RelayCommand(p => { if (p is KnownWiimoteViewModel k) RemoveKnown(k); }, _ => !IsBusy);
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
            RefreshCommand.RaiseCanExecuteChanged();
            RemoveCommand.RaiseCanExecuteChanged();
        }
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
        }
    }

    public string Instructions => _mode == PairMode.Permanent
        ? "Open the battery cover and press the small red SYNC button. The LEDs start blinking. " +
          "Then click “Search & pair”. Paired this way, the Wiimote reconnects by itself whenever " +
          "you press a button — also after swapping batteries."
        : "Hold 1 and 2 until the LEDs blink, then click “Search & pair”. The Wiimote forgets " +
          "the PC when it turns off, so you will need to pair again next time.";

    public string BluetoothStatus
    {
        get => _bluetoothStatus;
        private set => Set(ref _bluetoothStatus, value);
    }

    private void Start()
    {
        IsBusy = true;
        Messages.Clear();
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        var mode = _mode;

        Task.Run(() =>
        {
            try
            {
                var results = WiimoteBluetooth.PairDiscoverable(mode, Report, ct);
                int ok = results.Count(r => r.Success);
                if (ok > 0)
                    Report($"Done: {ok} Wiimote(s) paired. They show up in the main window within a few seconds.");
            }
            catch (Exception ex)
            {
                Report($"Pairing failed: {ex.Message}");
                Log.Error($"Pairing failed: {ex}");
            }
            finally
            {
                _ui.Post(() =>
                {
                    IsBusy = false;
                    RefreshKnown();
                });
            }
        });
    }

    private void Cancel()
    {
        _cts?.Cancel();
        Report("Cancelling after the current search round…");
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

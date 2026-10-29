using System.Collections.ObjectModel;
using WiiHub.Core;
using WiiHub.Core.Bluetooth;
using WiiHub.Core.Config;
using WiiHub.Core.Controllers;
using WiiHub.Core.Input;
using WiiHub.Core.Mapping;
using WiiHub.Core.Wiimote;
using static WiiHub.Core.Input.InputId;

namespace WiiHub.ViewModels;

/// <summary>One controller in the list plus everything shown when it is selected.</summary>
public sealed class ControllerViewModel : ObservableObject
{
    private const long FlashMs = 120;

    private readonly MainViewModel _main;
    private readonly long[] _lastActive = new long[InputCatalog.Count];
    private HashSet<InputId> _listenBaseline = [];
    private (double X, double Y, double Z)? _tiltRest;
    private DeviceKind _lastLiveKind = DeviceKind.Guitar;

    public ControllerViewModel(MainViewModel main, ControllerSlot slot)
    {
        _main = main;
        Slot = slot;

        AddBindingCommand = new RelayCommand(AddBinding);
        RemoveBindingCommand = new RelayCommand(p => { if (p is BindingViewModel b) RemoveBinding(b); });
        ListenCommand = new RelayCommand(p => { if (p is BindingViewModel b) StartListening(b); });
        CaptureKeyCommand = new RelayCommand(p => { if (p is BindingViewModel b) _main.BeginKeyCapture(b); });
        ResetMappingCommand = new RelayCommand(ResetMapping);
        IdentifyCommand = new RelayCommand(() => _ = Slot.IdentifyAsync(), () => IsConnected);
        ForgetCommand = new RelayCommand(Forget);
        RepairCommand = new RelayCommand(Repair, () => MacAddress is not null);
        CalibrateRestCommand = new RelayCommand(CalibrateRest, () => IsConnected);
        CalibrateTiltedCommand = new RelayCommand(CalibrateTilted, () => IsConnected && _tiltRest is not null);
        ResetTiltCommand = new RelayCommand(ResetTilt);
        TurnOffCommand = new RelayCommand(TurnOff, () => IsConnected && !_turningOff);
        StartRenameCommand = new RelayCommand(StartRename);
        CommitRenameCommand = new RelayCommand(CommitRename);
        CancelRenameCommand = new RelayCommand(CancelRename);

        _mappingOutput = slot.Config.Output == OutputMode.Keyboard ? OutputKind.Keyboard : OutputKind.Xbox360;
        LoadBindings();
        UpdateStatus();
    }

    public ControllerSlot Slot { get; }
    private ControllerConfig Config => Slot.Config;

    // ================================================================== settings

    public string Id => Config.Id;
    public string? MacAddress => BtAddress.Normalize(Config.Id);

    public const int MaxNameLength = 40;

    /// <summary>The label shown everywhere (sidebar, header, log). Never empty.</summary>
    public string Name => string.IsNullOrWhiteSpace(Config.Name) ? DefaultName : Config.Name;

    private string DefaultName => $"Wiimote {Config.PlayerIndex}";

    /// <summary>Hardware identity shown under the label, so a label can be traced to a physical Wiimote.</summary>
    public string HardwareText => MacAddress is null ? "Wiimote" : $"Wiimote · {MacAddress}";

    // ---------------- renaming

    private bool _isRenaming;
    public bool IsRenaming
    {
        get => _isRenaming;
        private set => Set(ref _isRenaming, value);
    }

    private string _editName = "";
    /// <summary>Text in the rename box while <see cref="IsRenaming"/>.</summary>
    public string EditName
    {
        get => _editName;
        set => Set(ref _editName, value ?? "");
    }

    public string NamePlaceholder => $"e.g. Red guitar, Aerosmith… (empty = “{DefaultName}”)";

    public RelayCommand StartRenameCommand { get; private set; } = null!;
    public RelayCommand CommitRenameCommand { get; private set; } = null!;
    public RelayCommand CancelRenameCommand { get; private set; } = null!;

    public void StartRename()
    {
        EditName = Config.Name;
        IsRenaming = true;
    }

    public void CommitRename()
    {
        if (!IsRenaming) return;
        string name = EditName.Trim();
        if (name.Length > MaxNameLength) name = name[..MaxNameLength].TrimEnd();
        IsRenaming = false;

        if (name == Config.Name) return;
        string old = Name;
        Config.Name = name.Length == 0 ? DefaultName : name;
        Raise(nameof(Name));
        _main.Hub.RequestSave();
        Log.Info($"Renamed “{old}” to “{Name}”.");
    }

    public void CancelRename() => IsRenaming = false;

    private static readonly IReadOnlyList<Option<int>> s_playerOptions =
        Enumerable.Range(1, 8).Select(i => new Option<int>(i, $"Player {i}")).ToList();

    // Instance properties (not static) because WPF bindings only see instance properties.
    public IReadOnlyList<Option<int>> PlayerOptions => s_playerOptions;

    public int PlayerIndex
    {
        get => Config.PlayerIndex;
        set
        {
            if (Config.PlayerIndex == value) return;
            // A name that is still the automatic "Wiimote N" follows the new player number.
            bool autoName = string.IsNullOrWhiteSpace(Config.Name) || Config.Name == DefaultName;
            Slot.SetPlayerIndex(value);
            if (autoName) Config.Name = DefaultName;
            Raise();
            Raise(nameof(LedPattern));
            Raise(nameof(Name));
            Raise(nameof(NamePlaceholder));
        }
    }

    /// <summary>Four booleans for the four player LEDs, as the Wiimote shows them.</summary>
    public bool[] LedPattern
    {
        get
        {
            byte m = WiimoteProtocol.LedMask(Config.PlayerIndex);
            return [(m & 1) != 0, (m & 2) != 0, (m & 4) != 0, (m & 8) != 0];
        }
    }

    public IReadOnlyList<Option<OutputMode>> OutputModeOptions => s_outputModeOptions;

    private static readonly IReadOnlyList<Option<OutputMode>> s_outputModeOptions =
    [
        new(OutputMode.Xbox360, "Xbox 360 controller"),
        new(OutputMode.Keyboard, "Keyboard"),
        new(OutputMode.Both, "Xbox 360 + keyboard"),
        new(OutputMode.Disabled, "Disabled"),
    ];

    public OutputMode OutputMode
    {
        get => Config.Output;
        set
        {
            if (Config.Output == value) return;
            Slot.SetOutputMode(value);
            Raise();
            Raise(nameof(UsesXbox));
            Raise(nameof(UsesKeyboard));
            if (value == OutputMode.Keyboard) MappingOutput = OutputKind.Keyboard;
            else if (value == OutputMode.Xbox360) MappingOutput = OutputKind.Xbox360;
            _main.Hub.RequestSave();
        }
    }

    public bool UsesXbox => Config.Output is OutputMode.Xbox360 or OutputMode.Both;
    public bool UsesKeyboard => Config.Output is OutputMode.Keyboard or OutputMode.Both;

    public bool ForwardRumble
    {
        get => Config.ForwardRumble;
        set
        {
            if (Config.ForwardRumble == value) return;
            Config.ForwardRumble = value;
            if (!value) Slot.Device?.SetRumble(false);
            Raise();
            _main.Hub.RequestSave();
        }
    }

    public bool KeepVirtualPad
    {
        get => Config.KeepVirtualPadWhenDisconnected;
        set
        {
            if (Config.KeepVirtualPadWhenDisconnected == value) return;
            Slot.SetKeepVirtualPad(value);
            Raise();
            _main.Hub.RequestSave();
        }
    }

    public bool TouchBarEnabled
    {
        get => Config.TouchBarEnabled;
        set
        {
            if (Config.TouchBarEnabled == value) return;
            Config.TouchBarEnabled = value;
            Raise();
            _main.Hub.RequestSave();
        }
    }

    // ================================================================== status

    private bool _isConnected;
    public bool IsConnected { get => _isConnected; private set => Set(ref _isConnected, value); }

    private string _statusText = "";
    public string StatusText { get => _statusText; private set => Set(ref _statusText, value); }

    private int _batteryPercent = -1;
    public int BatteryPercent { get => _batteryPercent; private set => Set(ref _batteryPercent, value); }

    private string _batteryText = "—";
    public string BatteryText { get => _batteryText; private set => Set(ref _batteryText, value); }

    private bool _batteryLow;
    public bool BatteryLow { get => _batteryLow; private set => Set(ref _batteryLow, value); }

    private string _extensionText = "Wiimote";
    public string ExtensionText { get => _extensionText; private set => Set(ref _extensionText, value); }

    private DeviceKind _liveKind = DeviceKind.Wiimote;
    public DeviceKind LiveKind
    {
        get => _liveKind;
        private set
        {
            if (Set(ref _liveKind, value))
            {
                Raise(nameof(IsGuitar));
                Raise(nameof(IsNunchuk));
            }
        }
    }

    public bool IsGuitar => _liveKind == DeviceKind.Guitar;
    public bool IsNunchuk => _liveKind == DeviceKind.Nunchuk;

    private string? _outputError;
    public string? OutputError
    {
        get => _outputError;
        private set
        {
            if (Set(ref _outputError, value)) Raise(nameof(HasOutputError));
        }
    }

    public bool HasOutputError => !string.IsNullOrEmpty(_outputError);

    private string _xboxOutputText = "—";
    public string XboxOutputText { get => _xboxOutputText; private set => Set(ref _xboxOutputText, value); }

    private string _keysOutputText = "—";
    public string KeysOutputText { get => _keysOutputText; private set => Set(ref _keysOutputText, value); }

    internal void UpdateStatus()
    {
        var dev = Slot.Device;
        bool connected = Slot.IsConnected;
        IsConnected = connected;
        var state = dev?.State;

        if (connected)
        {
            StatusText = "Connected";
            int battery = state?.BatteryPercent ?? -1;
            BatteryPercent = battery;
            BatteryText = battery < 0 ? "—" : $"{battery}%";
            BatteryLow = state?.BatteryLow ?? false;
        }
        else if (Slot.TurnedOff)
        {
            StatusText = "Turned off · press any button on the Wiimote to turn it on";
            BatteryPercent = -1;
            BatteryText = "—";
            BatteryLow = false;
        }
        else
        {
            StatusText = "Not connected · press any button on the Wiimote";
            BatteryPercent = -1;
            BatteryText = "—";
            BatteryLow = false;
        }

        var ext = connected ? state?.Extension ?? ExtensionType.None : ExtensionType.None;
        ExtensionText = ext switch
        {
            ExtensionType.None => connected ? "Wiimote" : "—",
            ExtensionType.Guitar => (state?.Guitar?.IsGh3 ?? false) ? "Guitar (GH3)" : "Guitar",
            ExtensionType.Nunchuk => "Nunchuk",
            ExtensionType.ClassicController => "Classic Controller (not supported yet)",
            ExtensionType.Drums => "Drums (not supported yet)",
            ExtensionType.Turntable => "Turntable (not supported yet)",
            ExtensionType.MotionPlus => "Motion Plus",
            _ => "Unknown extension",
        };

        OutputError = Slot.OutputError;
        if (_commandsConnectedState != connected)
        {
            _commandsConnectedState = connected;
            IdentifyCommand.RaiseCanExecuteChanged();
            TurnOffCommand.RaiseCanExecuteChanged();
            CalibrateRestCommand.RaiseCanExecuteChanged();
            CalibrateTiltedCommand.RaiseCanExecuteChanged();
        }
    }

    private bool? _commandsConnectedState;

    // ================================================================== live input

    private bool _wmA, _wmB, _wmOne, _wmTwo, _wmPlus, _wmMinus, _wmHome, _wmUp, _wmDown, _wmLeft, _wmRight;
    public bool WmA { get => _wmA; private set => Set(ref _wmA, value); }
    public bool WmB { get => _wmB; private set => Set(ref _wmB, value); }
    public bool WmOne { get => _wmOne; private set => Set(ref _wmOne, value); }
    public bool WmTwo { get => _wmTwo; private set => Set(ref _wmTwo, value); }
    public bool WmPlus { get => _wmPlus; private set => Set(ref _wmPlus, value); }
    public bool WmMinus { get => _wmMinus; private set => Set(ref _wmMinus, value); }
    public bool WmHome { get => _wmHome; private set => Set(ref _wmHome, value); }
    public bool WmUp { get => _wmUp; private set => Set(ref _wmUp, value); }
    public bool WmDown { get => _wmDown; private set => Set(ref _wmDown, value); }
    public bool WmLeft { get => _wmLeft; private set => Set(ref _wmLeft, value); }
    public bool WmRight { get => _wmRight; private set => Set(ref _wmRight, value); }

    private double _accelX = 50, _accelY = 50, _accelZ = 50;
    /// <summary>Accelerometer per axis, 0..100 (50 = 0 g).</summary>
    public double AccelX { get => _accelX; private set => Set(ref _accelX, value); }
    public double AccelY { get => _accelY; private set => Set(ref _accelY, value); }
    public double AccelZ { get => _accelZ; private set => Set(ref _accelZ, value); }

    private bool _green, _red, _yellow, _blue, _orange, _strumUp, _strumDown, _gPlus, _gMinus, _tiltOn;
    public bool GreenOn { get => _green; private set => Set(ref _green, value); }
    public bool RedOn { get => _red; private set => Set(ref _red, value); }
    public bool YellowOn { get => _yellow; private set => Set(ref _yellow, value); }
    public bool BlueOn { get => _blue; private set => Set(ref _blue, value); }
    public bool OrangeOn { get => _orange; private set => Set(ref _orange, value); }
    public bool StrumUpOn { get => _strumUp; private set => Set(ref _strumUp, value); }
    public bool StrumDownOn { get => _strumDown; private set => Set(ref _strumDown, value); }
    public bool GuitarPlusOn { get => _gPlus; private set => Set(ref _gPlus, value); }
    public bool GuitarMinusOn { get => _gMinus; private set => Set(ref _gMinus, value); }
    public bool TiltOn { get => _tiltOn; private set => Set(ref _tiltOn, value); }

    private bool _tGreen, _tRed, _tYellow, _tBlue, _tOrange;
    public bool TouchGreenOn { get => _tGreen; private set => Set(ref _tGreen, value); }
    public bool TouchRedOn { get => _tRed; private set => Set(ref _tRed, value); }
    public bool TouchYellowOn { get => _tYellow; private set => Set(ref _tYellow, value); }
    public bool TouchBlueOn { get => _tBlue; private set => Set(ref _tBlue, value); }
    public bool TouchOrangeOn { get => _tOrange; private set => Set(ref _tOrange, value); }

    private double _whammy, _tiltPercent;
    /// <summary>0..100.</summary>
    public double WhammyPercent { get => _whammy; private set => Set(ref _whammy, value); }
    /// <summary>0..100.</summary>
    public double TiltPercent { get => _tiltPercent; private set => Set(ref _tiltPercent, value); }

    public double TiltThresholdPercent
    {
        get => Math.Round(Config.Tilt.Threshold * 100);
        set
        {
            double t = Math.Clamp(value / 100.0, 0.15, 0.9);
            if (Math.Abs(t - Config.Tilt.Threshold) < 0.001) return;
            Config.Tilt.Threshold = t;
            Raise();
            _main.Hub.RequestSave();
        }
    }

    public string TiltCalibrationText => Config.Tilt.Calibrated
        ? $"Calibrated (axis {Config.Tilt.Axis}). Only tilting the neck up counts."
        : "Not calibrated: tilting either way counts. Calibrate for best results.";

    private string _tiltStepText = "1. Hold the guitar normally and click “Rest”.";
    public string TiltStepText { get => _tiltStepText; private set => Set(ref _tiltStepText, value); }

    private double _stickX, _stickY, _ncStickX, _ncStickY;
    /// <summary>Stick positions, -1..1 (Y up = positive).</summary>
    public double StickX { get => _stickX; private set => Set(ref _stickX, value); }
    public double StickY { get => _stickY; private set => Set(ref _stickY, value); }
    public double NunchukStickX { get => _ncStickX; private set => Set(ref _ncStickX, value); }
    public double NunchukStickY { get => _ncStickY; private set => Set(ref _ncStickY, value); }

    private bool _ncC, _ncZ;
    public bool NunchukC { get => _ncC; private set => Set(ref _ncC, value); }
    public bool NunchukZ { get => _ncZ; private set => Set(ref _ncZ, value); }

    private string _rawText = "";
    /// <summary>Raw values, handy when something behaves oddly.</summary>
    public string RawText { get => _rawText; private set => Set(ref _rawText, value); }

    /// <summary>Called by the main view model at ~30 Hz.</summary>
    internal void Tick(bool isSelected)
    {
        var recent = Slot.TakeRecentlyActive();
        if (!isSelected) return;

        long now = Environment.TickCount64;
        foreach (var id in recent) _lastActive[(int)id] = now;

        var s = Slot.Latest;
        bool P(InputId id) => s.IsPressed(id) || now - _lastActive[(int)id] < FlashMs;

        LiveKind = Slot.IsConnected ? s.Kind : _lastLiveKind;
        if (Slot.IsConnected && s.Kind != _lastLiveKind)
        {
            _lastLiveKind = s.Kind;
            MappingDevice = s.Kind; // follow the plugged-in extension
        }

        WmA = P(WiimoteA); WmB = P(WiimoteB); WmOne = P(WiimoteOne); WmTwo = P(WiimoteTwo);
        WmPlus = P(WiimotePlus); WmMinus = P(WiimoteMinus); WmHome = P(WiimoteHome);
        WmUp = P(WiimoteUp); WmDown = P(WiimoteDown); WmLeft = P(WiimoteLeft); WmRight = P(WiimoteRight);
        AccelX = 50 + s[WiimoteAccelX] * 50;
        AccelY = 50 + s[WiimoteAccelY] * 50;
        AccelZ = 50 + s[WiimoteAccelZ] * 50;

        GreenOn = P(GuitarGreen); RedOn = P(GuitarRed); YellowOn = P(GuitarYellow);
        BlueOn = P(GuitarBlue); OrangeOn = P(GuitarOrange);
        StrumUpOn = P(GuitarStrumUp); StrumDownOn = P(GuitarStrumDown);
        GuitarPlusOn = P(GuitarPlus); GuitarMinusOn = P(GuitarMinus);
        TouchGreenOn = P(GuitarTouchGreen); TouchRedOn = P(GuitarTouchRed); TouchYellowOn = P(GuitarTouchYellow);
        TouchBlueOn = P(GuitarTouchBlue); TouchOrangeOn = P(GuitarTouchOrange);
        WhammyPercent = Math.Round(s[GuitarWhammy] * 100);
        TiltPercent = Math.Round(s[GuitarTiltAmount] * 100);
        TiltOn = P(GuitarTilt);
        StickX = s[GuitarStickX];
        StickY = s[GuitarStickY];

        NunchukC = P(InputId.NunchukC);
        NunchukZ = P(InputId.NunchukZ);
        NunchukStickX = s[InputId.NunchukStickX];
        NunchukStickY = s[InputId.NunchukStickY];

        var raw = s.Raw;
        RawText = raw.Guitar is { } g
            ? $"whammy {g.WhammyRaw}  stick {g.StickXRaw},{g.StickYRaw}  touch 0x{g.TouchBarRaw:X2}  tilt axis {s.TiltAxisG:+0.00;-0.00} g"
            : raw.HasAccel ? $"accel {raw.Accel.X},{raw.Accel.Y},{raw.Accel.Z}" : "";

        XboxOutputText = Slot.HasVirtualPad ? Slot.LastXbox.Describe() : "—";
        KeysOutputText = MappingEngine.DescribeKeys(Slot.HeldKeys);

        // Highlight bindings that are currently firing.
        bool sameKind = s.Kind == _mappingDevice;
        foreach (var b in Bindings)
        {
            var model = b.ToModel();
            b.IsActive = sameKind && (MappingEngine.IsActive(s, model) || IsFlashing(model, now));
        }

        HandleListening(recent);
    }

    private bool IsFlashing(Binding b, long now) =>
        InputCatalog.Get(b.Source).Kind == InputKind.Button && !b.Invert && now - _lastActive[(int)b.Source] < FlashMs;

    // ================================================================== mapping editor

    public ObservableCollection<BindingViewModel> Bindings { get; } = [];

    public IReadOnlyList<Option<DeviceKind>> MappingDeviceOptions => s_mappingDeviceOptions;

    private static readonly IReadOnlyList<Option<DeviceKind>> s_mappingDeviceOptions =
    [
        new(DeviceKind.Guitar, "Guitar"),
        new(DeviceKind.Wiimote, "Wiimote only"),
        new(DeviceKind.Nunchuk, "Wiimote + Nunchuk"),
    ];

    public IReadOnlyList<Option<OutputKind>> MappingOutputOptions => s_mappingOutputOptions;

    private static readonly IReadOnlyList<Option<OutputKind>> s_mappingOutputOptions =
    [
        new(OutputKind.Xbox360, "Xbox 360 controller"),
        new(OutputKind.Keyboard, "Keyboard"),
    ];

    private DeviceKind _mappingDevice = DeviceKind.Guitar;
    public DeviceKind MappingDevice
    {
        get => _mappingDevice;
        set
        {
            if (Set(ref _mappingDevice, value)) LoadBindings();
        }
    }

    private OutputKind _mappingOutput;
    public OutputKind MappingOutput
    {
        get => _mappingOutput;
        set
        {
            if (Set(ref _mappingOutput, value)) LoadBindings();
        }
    }

    private IReadOnlyList<Option<InputId>> _sourceOptions = [];
    public IReadOnlyList<Option<InputId>> SourceOptions { get => _sourceOptions; private set => Set(ref _sourceOptions, value); }

    private IReadOnlyList<Option<int>> _targetOptions = [];
    public IReadOnlyList<Option<int>> TargetOptions { get => _targetOptions; private set => Set(ref _targetOptions, value); }

    public bool MappingIsKeyboard => _mappingOutput == OutputKind.Keyboard;

    public RelayCommand AddBindingCommand { get; }
    public RelayCommand RemoveBindingCommand { get; }
    public RelayCommand ListenCommand { get; }
    public RelayCommand CaptureKeyCommand { get; }
    public RelayCommand ResetMappingCommand { get; }

    private void LoadBindings()
    {
        _main.CancelKeyCapture();
        _loading = true;
        try
        {
            // Remove the old rows before swapping the option lists, so no ComboBox can push a
            // stale value into a row while its item list changes underneath it.
            Bindings.Clear();
            var set = Config.GetMapping(_mappingDevice, _mappingOutput);
            SourceOptions = InputCatalog.For(_mappingDevice).Select(i => new Option<InputId>(i.Id, i.Name)).ToList();
            TargetOptions = BuildTargetOptions(set.Bindings.Select(b => b.Target));
            foreach (var b in set.Bindings) Bindings.Add(new BindingViewModel(this, b, _mappingOutput));
            Raise(nameof(MappingIsKeyboard));
        }
        finally { _loading = false; }
    }

    private bool _loading;

    private IReadOnlyList<Option<int>> BuildTargetOptions(IEnumerable<int> used)
    {
        if (_mappingOutput == OutputKind.Xbox360)
            return XboxTargets.All.Select(t => new Option<int>((int)t, XboxTargets.DisplayName(t))).ToList();

        var list = Keys.All.Select(k => new Option<int>(k.VirtualKey, k.Name)).ToList();
        foreach (int vk in used)
        {
            if (list.All(o => o.Value != vk)) list.Add(new Option<int>(vk, Keys.Name(vk)));
        }
        return list;
    }

    /// <summary>Writes the editor rows back to the settings and applies them immediately.</summary>
    internal void CommitMapping()
    {
        if (_loading) return;
        var set = Config.GetMapping(_mappingDevice, _mappingOutput);
        set.Bindings = Bindings.Select(b => b.ToModel()).ToList();
        Slot.RefreshMappings();
        _main.Hub.RequestSave();
    }

    /// <summary>Called when a key was captured that is not yet in the target list.</summary>
    internal void EnsureTargetOption(int vk)
    {
        if (TargetOptions.Any(o => o.Value == vk)) return;
        TargetOptions = BuildTargetOptions(Bindings.Select(b => b.Target).Append(vk));
    }

    private void AddBinding()
    {
        var source = SourceOptions.FirstOrDefault(o => InputCatalog.Get(o.Value).Group != InputGroup.Wiimote)
                     ?? SourceOptions[0];
        int target = _mappingOutput == OutputKind.Xbox360 ? (int)XboxTarget.A : 'A';
        var vm = new BindingViewModel(this, new Binding(source.Value, target), _mappingOutput);
        Bindings.Add(vm);
        CommitMapping();
        StartListening(vm);
    }

    private void RemoveBinding(BindingViewModel b)
    {
        Bindings.Remove(b);
        CommitMapping();
    }

    private void ResetMapping()
    {
        string what = $"{MappingDeviceOptions.First(o => o.Value == _mappingDevice).Name} → " +
                      $"{MappingOutputOptions.First(o => o.Value == _mappingOutput).Name}";
        if (!_main.Ui.Confirm("Reset mapping", $"Reset the {what} mapping to the defaults?")) return;
        Config.GetMapping(_mappingDevice, _mappingOutput).Bindings = DefaultMappings.Create(_mappingDevice, _mappingOutput);
        Slot.RefreshMappings();
        _main.Hub.RequestSave();
        LoadBindings();
    }

    private void StartListening(BindingViewModel b)
    {
        foreach (var other in Bindings) other.IsListening = false;
        if (!Slot.IsConnected)
        {
            Log.Warn("Connect the controller first, then use Detect.");
            return;
        }
        // Inputs already active right now (held buttons, sticks resting off-centre) are ignored.
        var s = Slot.Latest;
        _listenBaseline = InputCatalog.Inputs
            .Where(i => i.Kind == InputKind.Bipolar ? Math.Abs(s[i.Id]) >= 0.6f : s[i.Id] >= 0.5f)
            .Select(i => i.Id)
            .ToHashSet();
        b.IsListening = true;
    }

    private void HandleListening(HashSet<InputId> recent)
    {
        var listening = Bindings.FirstOrDefault(b => b.IsListening);
        if (listening is null || recent.Count == 0) return;

        var allowed = SourceOptions.Select(o => o.Value).ToHashSet();
        var candidates = recent
            .Where(id => allowed.Contains(id) && !_listenBaseline.Contains(id))
            .Where(id => id is not (WiimoteAccelX or WiimoteAccelY or WiimoteAccelZ or GuitarTiltAmount))
            .Select(InputCatalog.Get)
            .OrderBy(i => i.Kind == InputKind.Button ? 0 : 1)
            .ThenBy(i => i.Group == InputGroup.Wiimote ? 1 : 0) // prefer extension inputs
            .ToList();
        if (candidates.Count == 0) return;

        listening.IsListening = false;
        listening.Source = candidates[0].Id;
    }

    // ================================================================== tilt calibration

    public RelayCommand CalibrateRestCommand { get; }
    public RelayCommand CalibrateTiltedCommand { get; }
    public RelayCommand ResetTiltCommand { get; }

    private void CalibrateRest()
    {
        var s = Slot.Latest;
        _tiltRest = (s[WiimoteAccelX], s[WiimoteAccelY], s[WiimoteAccelZ]);
        TiltStepText = "2. Now tilt the neck up (like Star Power) and click “Tilted”.";
        CalibrateTiltedCommand.RaiseCanExecuteChanged();
    }

    private void CalibrateTilted()
    {
        if (_tiltRest is not { } rest) return;
        var s = Slot.Latest;
        var tilted = (X: (double)s[WiimoteAccelX], Y: (double)s[WiimoteAccelY], Z: (double)s[WiimoteAccelZ]);
        double dx = tilted.X - rest.X, dy = tilted.Y - rest.Y, dz = tilted.Z - rest.Z;

        AccelAxis axis = AccelAxis.X;
        double best = Math.Abs(dx);
        if (Math.Abs(dy) > best) { axis = AccelAxis.Y; best = Math.Abs(dy); }
        if (Math.Abs(dz) > best) { axis = AccelAxis.Z; best = Math.Abs(dz); }

        if (best < 0.2)
        {
            TiltStepText = "That was not much of a tilt. Tilt the neck further up and click “Tilted” again.";
            return;
        }

        Config.Tilt.Axis = axis;
        Config.Tilt.RestG = axis switch { AccelAxis.X => rest.X, AccelAxis.Y => rest.Y, _ => rest.Z };
        Config.Tilt.TiltedG = axis switch { AccelAxis.X => tilted.X, AccelAxis.Y => tilted.Y, _ => tilted.Z };
        Config.Tilt.Calibrated = true;
        _tiltRest = null;
        TiltStepText = "Done! Tilt the guitar to test it.";
        Raise(nameof(TiltCalibrationText));
        CalibrateTiltedCommand.RaiseCanExecuteChanged();
        _main.Hub.RequestSave();
        Log.Info($"{Config.Name}: tilt calibrated on axis {axis} (rest {Config.Tilt.RestG:0.00} g, tilted {Config.Tilt.TiltedG:0.00} g).");
    }

    private void ResetTilt()
    {
        Config.Tilt = new TiltSettings();
        _tiltRest = null;
        TiltStepText = "1. Hold the guitar normally and click “Rest”.";
        Raise(nameof(TiltCalibrationText));
        Raise(nameof(TiltThresholdPercent));
        CalibrateTiltedCommand.RaiseCanExecuteChanged();
        _main.Hub.RequestSave();
    }

    // ================================================================== controller actions

    public RelayCommand IdentifyCommand { get; }
    public RelayCommand TurnOffCommand { get; }

    private bool _turningOff;

    private void TurnOff()
    {
        _turningOff = true;
        TurnOffCommand.RaiseCanExecuteChanged();
        Task.Run(() =>
        {
            bool ok = _main.Hub.TurnOff(Slot, out string message);
            _main.Ui.Post(() =>
            {
                _turningOff = false;
                TurnOffCommand.RaiseCanExecuteChanged();
                UpdateStatus();
                if (!ok) _main.Ui.ShowError("Couldn't turn off the Wiimote", message);
            });
        });
    }
    public RelayCommand ForgetCommand { get; }
    public RelayCommand RepairCommand { get; }

    private void Forget()
    {
        if (!_main.Ui.Confirm("Forget controller",
                $"Forget “{Name}”? Its settings and mappings are deleted. " +
                "Windows keeps the Bluetooth pairing (remove it in the pairing window if you want).")) return;
        _main.Hub.RemoveSlot(Slot);
    }

    private void Repair()
    {
        if (MacAddress is null) return;
        ulong address = Convert.ToUInt64(MacAddress.Replace(":", ""), 16);
        Log.Info($"Re-enabling the HID service for {MacAddress}…");
        Task.Run(() => WiimoteBluetooth.RepairHidService(address, Log.Info));
    }
}

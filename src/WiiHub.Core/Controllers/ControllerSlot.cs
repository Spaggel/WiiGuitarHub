using WiiHub.Core.Config;
using WiiHub.Core.Input;
using WiiHub.Core.Mapping;
using WiiHub.Core.Output;
using WiiHub.Core.Wiimote;

namespace WiiHub.Core.Controllers;

/// <summary>
/// A remembered controller. The slot outlives the Bluetooth connection: when the Wiimote drops
/// out (battery swap) and reconnects, it is re-attached to the same slot, with the same player
/// number, mapping and — optionally — the same virtual Xbox controller.
/// </summary>
public sealed class ControllerSlot : IDisposable
{
    private readonly IXboxPadFactory? _padFactory;
    private readonly InputNormalizer _normalizer = new();
    private readonly object _outputLock = new();
    private readonly HashSet<int> _keys = [];

    private volatile WiimoteDevice? _device;
    private volatile InputSnapshot _latest = InputSnapshot.Empty;
    private volatile IReadOnlyDictionary<(DeviceKind, OutputKind), Binding[]> _compiled =
        new Dictionary<(DeviceKind, OutputKind), Binding[]>();
    private IXboxPad? _pad;
    private XboxReport _lastXbox;
    private long _lastXboxSubmit;
    private bool _everConnected;
    private long _pressedSincePoll;
    private ExtensionType _lastExtension = ExtensionType.None;
    private int _lastBattery = -1;
    private bool _disposed;
    private long _lastActivity = Environment.TickCount64;
    private volatile bool _turnedOff;

    /// <summary>Inputs that never count as "someone is using it": gravity on the motion sensor and
    /// tilt (a guitar leaning against a wall is permanently tilted).</summary>
    private static readonly long PassiveInputs =
        (1L << (int)InputId.WiimoteAccelX) | (1L << (int)InputId.WiimoteAccelY) | (1L << (int)InputId.WiimoteAccelZ) |
        (1L << (int)InputId.GuitarTilt) | (1L << (int)InputId.GuitarTiltAmount);

    public ControllerSlot(ControllerConfig config, IXboxPadFactory? padFactory)
    {
        Config = config;
        _padFactory = padFactory;
        RefreshMappings();
    }

    public ControllerConfig Config { get; }
    public string Id => Config.Id;
    public WiimoteDevice? Device => _device;
    public bool IsConnected => _device is { IsConnected: true };
    public InputSnapshot Latest => _latest;

    /// <summary>Last state sent to the virtual Xbox controller (for the UI).</summary>
    public XboxReport LastXbox
    {
        get { lock (_outputLock) return _lastXbox; }
    }

    /// <summary>Keys currently held by this controller (for the UI).</summary>
    public int[] HeldKeys
    {
        get { lock (_outputLock) return _keys.ToArray(); }
    }

    public string? OutputError { get; private set; }
    public bool HasVirtualPad
    {
        get { lock (_outputLock) return _pad is not null; }
    }

    /// <summary>Connection, extension, battery or output status changed (any thread).</summary>
    public event Action<ControllerSlot>? Changed;

    // ------------------------------------------------------------------ device attach/detach

    internal void Attach(WiimoteDevice device)
    {
        _device = device;
        _everConnected = true;
        _turnedOff = false;
        Interlocked.Exchange(ref _lastActivity, Environment.TickCount64);
        _normalizer.Reset();
        Config.LastSeen = DateTime.Now;
        device.StateChanged += OnState;
        device.ExtensionChanged += OnExtensionChanged;
        device.SetPlayerLeds(Config.PlayerIndex);
        EnsureOutputs();
        Changed?.Invoke(this);
    }

    internal void Detach(WiimoteDevice device)
    {
        if (_device != device) return;
        device.StateChanged -= OnState;
        device.ExtensionChanged -= OnExtensionChanged;
        _device = null;
        _latest = InputSnapshot.Empty;

        lock (_outputLock)
        {
            // Never leave a fret or key stuck while the controller is gone.
            KeyboardOutput.Instance.ReleaseAll(this);
            _keys.Clear();
            _lastXbox = default;
            _pad?.Submit(default);
        }
        EnsureOutputs();
        Changed?.Invoke(this);
    }

    private void OnExtensionChanged(WiimoteDevice dev, ExtensionType type)
    {
        _normalizer.Reset();
        Changed?.Invoke(this);
    }

    private void OnState(WiimoteDevice dev, WiimoteState state)
    {
        var snapshot = _normalizer.Normalize(state, dev.Calibration, Config.Tilt, Config.TouchBarEnabled);
        _latest = snapshot;
        RecordPresses(snapshot);
        ApplyOutputs(snapshot);

        if (state.Extension != _lastExtension || state.BatteryPercent != _lastBattery)
        {
            _lastExtension = state.Extension;
            _lastBattery = state.BatteryPercent;
            Changed?.Invoke(this);
        }
    }

    // ------------------------------------------------------------------ input checking helpers

    private void RecordPresses(InputSnapshot s)
    {
        long mask = 0;
        var values = s.Values;
        for (int i = 0; i < values.Length; i++)
        {
            var kind = InputCatalog.Inputs[i].Kind;
            bool active = kind == InputKind.Bipolar ? Math.Abs(values[i]) >= 0.6f : values[i] >= 0.5f;
            if (active) mask |= 1L << i;
        }
        if (mask != 0) Interlocked.Or(ref _pressedSincePoll, mask);
        if ((mask & ~PassiveInputs) != 0) Interlocked.Exchange(ref _lastActivity, Environment.TickCount64);
    }

    /// <summary>
    /// Inputs that were active at any moment since the previous call. Lets the UI show quick
    /// taps (a fast strum) that happen between two screen refreshes, and powers "press to bind".
    /// </summary>
    public HashSet<InputId> TakeRecentlyActive()
    {
        long mask = Interlocked.Exchange(ref _pressedSincePoll, 0);
        var set = new HashSet<InputId>();
        for (int i = 0; i < InputCatalog.Count; i++)
        {
            if ((mask & (1L << i)) != 0) set.Add((InputId)i);
        }
        return set;
    }

    // ------------------------------------------------------------------ outputs

    /// <summary>Call after editing <see cref="ControllerConfig.Mappings"/>.</summary>
    public void RefreshMappings()
    {
        var dict = new Dictionary<(DeviceKind, OutputKind), Binding[]>();
        foreach (DeviceKind d in Enum.GetValues<DeviceKind>())
        foreach (OutputKind o in Enum.GetValues<OutputKind>())
            dict[(d, o)] = Config.GetMapping(d, o).Bindings.ToArray();
        _compiled = dict;
        var latest = _latest;
        if (IsConnected) ApplyOutputs(latest);
    }

    public void SetOutputMode(OutputMode mode)
    {
        Config.Output = mode;
        EnsureOutputs();
        if (IsConnected) ApplyOutputs(_latest);
        Changed?.Invoke(this);
    }

    public void SetPlayerIndex(int playerIndex)
    {
        Config.PlayerIndex = playerIndex;
        _device?.SetPlayerLeds(playerIndex);
        Changed?.Invoke(this);
    }

    public void SetKeepVirtualPad(bool keep)
    {
        Config.KeepVirtualPadWhenDisconnected = keep;
        EnsureOutputs();
    }

    /// <summary>How long nobody pressed or moved anything on this controller.</summary>
    public TimeSpan IdleTime => TimeSpan.FromMilliseconds(Environment.TickCount64 - Interlocked.Read(ref _lastActivity));

    /// <summary>True after <see cref="ControllerHub.TurnOff"/> until the Wiimote reconnects.</summary>
    public bool TurnedOff => _turnedOff;

    internal void MarkTurnedOff()
    {
        _turnedOff = true;
        EnsureOutputs();
        Changed?.Invoke(this);
    }

    /// <summary>Short rumble so the user can tell which physical Wiimote this slot is.</summary>
    public async Task IdentifyAsync()
    {
        var dev = _device;
        if (dev is null) return;
        for (int i = 0; i < 2; i++)
        {
            dev.SetRumble(true);
            await Task.Delay(150);
            dev.SetRumble(false);
            await Task.Delay(120);
        }
    }

    private bool WantsXbox => Config.Output is OutputMode.Xbox360 or OutputMode.Both;
    private bool WantsKeyboard => Config.Output is OutputMode.Keyboard or OutputMode.Both;

    private void EnsureOutputs()
    {
        bool changed = false;
        lock (_outputLock)
        {
            if (_disposed) return;
            // A deliberately turned-off Wiimote also unplugs its virtual controller; a dropped one
            // (battery swap) keeps it if the user asked for that.
            bool needPad = WantsXbox &&
                           (IsConnected || (_everConnected && Config.KeepVirtualPadWhenDisconnected && !_turnedOff));

            if (needPad && _pad is null && _padFactory is not null)
            {
                try
                {
                    _pad = _padFactory.Create();
                    _pad.RumbleReceived += OnRumble;
                    _lastXbox = default;
                    OutputError = null;
                    Log.Info($"{Config.Name}: virtual Xbox 360 controller connected.");
                }
                catch (Exception ex)
                {
                    _pad = null;
                    OutputError = $"Virtual Xbox controller unavailable: {ex.Message}";
                    Log.Error($"{Config.Name}: {OutputError}");
                }
                changed = true;
            }
            else if (needPad && _padFactory is null)
            {
                OutputError = "Virtual Xbox controller unavailable (ViGEmBus driver not installed).";
            }
            else if (!needPad && _pad is not null)
            {
                _pad.RumbleReceived -= OnRumble;
                _pad.Dispose();
                _pad = null;
                OutputError = null;
                Log.Info($"{Config.Name}: virtual Xbox 360 controller removed.");
                changed = true;
            }
            else if (!needPad)
            {
                OutputError = null;
            }

            if (!WantsKeyboard && _keys.Count > 0)
            {
                KeyboardOutput.Instance.ReleaseAll(this);
                _keys.Clear();
            }
        }
        if (changed) Changed?.Invoke(this);
    }

    private void ApplyOutputs(InputSnapshot s)
    {
        var compiled = _compiled;
        lock (_outputLock)
        {
            if (_disposed) return;

            if (_pad is not null && WantsXbox && compiled.TryGetValue((s.Kind, OutputKind.Xbox360), out var xb))
            {
                var report = MappingEngine.EvaluateXbox(s, xb);
                long now = Environment.TickCount64;
                // Resend unchanged state now and then; cheap insurance against a dropped report.
                if (!report.Equals(_lastXbox) || now - _lastXboxSubmit > 250)
                {
                    try { _pad.Submit(report); }
                    catch (Exception ex) { Log.Error($"{Config.Name}: Xbox submit failed: {ex.Message}"); }
                    _lastXbox = report;
                    _lastXboxSubmit = now;
                }
            }

            if (WantsKeyboard && compiled.TryGetValue((s.Kind, OutputKind.Keyboard), out var kb))
            {
                var pressed = new HashSet<int>();
                MappingEngine.EvaluateKeys(s, kb, pressed);
                if (!pressed.SetEquals(_keys))
                {
                    KeyboardOutput.Instance.SetKeys(this, pressed);
                    _keys.Clear();
                    _keys.UnionWith(pressed);
                }
            }
        }
    }

    private void OnRumble(byte large, byte small)
    {
        if (!Config.ForwardRumble) return;
        _device?.SetRumble(large > 16 || small > 16);
    }

    public void Dispose()
    {
        lock (_outputLock)
        {
            if (_disposed) return;
            _disposed = true;
            KeyboardOutput.Instance.ReleaseAll(this);
            _keys.Clear();
            if (_pad is not null)
            {
                _pad.RumbleReceived -= OnRumble;
                _pad.Dispose();
                _pad = null;
            }
        }
        var dev = _device;
        if (dev is not null)
        {
            dev.StateChanged -= OnState;
            dev.ExtensionChanged -= OnExtensionChanged;
        }
    }
}

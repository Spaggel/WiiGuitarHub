using System.Collections.Concurrent;
using WiiHub.Core.Config;
using WiiHub.Core.Hid;
using WiiHub.Core.Output;
using WiiHub.Core.Wiimote;

namespace WiiHub.Core.Controllers;

/// <summary>
/// Watches for Wiimote HID devices, connects them to their (remembered) slots and reconnects
/// them automatically. A Wiimote paired with the SYNC button reconnects on its own when a button
/// is pressed; Windows then re-creates its HID device and the next poll picks it up.
/// </summary>
public sealed class ControllerHub : IDisposable
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(1500);
    private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(2);

    private readonly AppConfig _config;
    private readonly ConfigStore _store;
    private readonly object _slotsLock = new();
    private readonly List<ControllerSlot> _slots = [];
    private readonly ConcurrentDictionary<string, WiimoteDevice> _attachedPaths = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, byte> _connecting = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, long> _retryAfter = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, byte> _turningOff = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, long> _autoOffBlockedUntil = new(StringComparer.OrdinalIgnoreCase);
    private Timer? _timer;
    private int _polling;
    private volatile bool _saveRequested;
    private volatile bool _disposed;

    public ControllerHub(AppConfig config, ConfigStore store, IXboxPadFactory? padFactory)
    {
        _config = config;
        _store = store;
        PadFactory = padFactory;
        foreach (var c in config.Controllers)
            _slots.Add(CreateSlot(c));
    }

    public IXboxPadFactory? PadFactory { get; }
    public AppConfig Config => _config;

    public IReadOnlyList<ControllerSlot> Slots
    {
        get { lock (_slotsLock) return _slots.ToArray(); }
    }

    /// <summary>Raised from a background thread.</summary>
    public event Action<ControllerSlot>? SlotAdded;

    /// <summary>Raised from the calling thread.</summary>
    public event Action<ControllerSlot>? SlotRemoved;

    /// <summary>A controller's battery ran low (raised once per drop, from a background thread).</summary>
    public event Action<ControllerSlot, int>? LowBattery;

    public void Start()
    {
        _timer = new Timer(_ => Poll(), null, TimeSpan.Zero, PollInterval);
    }

    /// <summary>Ask for the settings to be written on the next poll (debounced, any thread).</summary>
    public void RequestSave() => _saveRequested = true;

    public void SaveNow()
    {
        _saveRequested = false;
        _store.Save(_config);
    }

    /// <summary>Forgets a controller: disconnects it here and removes its settings.</summary>
    public void RemoveSlot(ControllerSlot slot)
    {
        lock (_slotsLock)
        {
            if (!_slots.Remove(slot)) return;
            _config.Controllers.Remove(slot.Config);
        }
        var dev = slot.Device;
        slot.Dispose();
        if (dev is not null)
        {
            _attachedPaths.TryRemove(dev.DevicePath, out _);
            dev.Dispose();
            // Don't immediately grab it again; if Windows still has it, it reappears after a while.
            _retryAfter[dev.DevicePath] = Environment.TickCount64 + 10_000;
        }
        RequestSave();
        SlotRemoved?.Invoke(slot);
    }

    // ------------------------------------------------------------------ polling

    private void Poll()
    {
        if (_disposed || Interlocked.Exchange(ref _polling, 1) == 1) return;
        try
        {
            List<HidDeviceInfo> found;
            try
            {
                found = HidDevice.Enumerate(WiimoteProtocol.VendorId,
                    WiimoteProtocol.ProductIdOriginal, WiimoteProtocol.ProductIdMotionPlusInside);
            }
            catch (Exception ex)
            {
                Log.Error($"HID enumeration failed: {ex.Message}");
                return;
            }

            long now = Environment.TickCount64;
            foreach (var info in found)
            {
                if (_attachedPaths.ContainsKey(info.Path)) continue;
                if (_retryAfter.TryGetValue(info.Path, out long after) && now < after) continue;
                if (!_connecting.TryAdd(info.Path, 0)) continue;
                _ = Task.Run(() => ConnectAsync(info));
            }

            CheckIdle();
            if (_saveRequested) SaveNow();
        }
        finally
        {
            Volatile.Write(ref _polling, 0);
        }
    }

    private async Task ConnectAsync(HidDeviceInfo info)
    {
        try
        {
            var existing = FindSlot(info.Serial, info.Path);
            int player = existing?.Config.PlayerIndex ?? NextFreePlayerIndex();

            var dev = await WiimoteDevice.ConnectAsync(info, player).ConfigureAwait(false);
            if (dev is null)
            {
                // Paired but asleep / out of range. Normal; try again later.
                _retryAfter[info.Path] = Environment.TickCount64 + (long)RetryDelay.TotalMilliseconds;
                return;
            }
            if (_disposed)
            {
                dev.Dispose();
                return;
            }

            ControllerSlot? slot;
            bool added = false;
            lock (_slotsLock)
            {
                slot = _slots.FirstOrDefault(s => s.Id.Equals(dev.Id, StringComparison.OrdinalIgnoreCase));
                if (slot is null)
                {
                    var cfg = new ControllerConfig
                    {
                        Id = dev.Id,
                        Name = $"Wiimote {player}",
                        PlayerIndex = player,
                    };
                    _config.Controllers.Add(cfg);
                    slot = CreateSlot(cfg);
                    _slots.Add(slot);
                    added = true;
                }
            }

            if (slot.Device is { IsConnected: true })
            {
                // Same Wiimote exposed twice; keep the first connection.
                dev.Dispose();
                _retryAfter[info.Path] = Environment.TickCount64 + 30_000;
                return;
            }

            _attachedPaths[info.Path] = dev;
            dev.Disconnected += OnDeviceDisconnected;
            if (added) SlotAdded?.Invoke(slot);
            slot.Attach(dev);
            Log.Info($"{slot.Config.Name} connected (player {slot.Config.PlayerIndex}, {dev.Id}).");
            RequestSave();

            // If it already dropped while we were wiring it up, clean up now.
            if (!dev.IsConnected) OnDeviceDisconnected(dev);
        }
        catch (Exception ex)
        {
            Log.Error($"Connecting {info.Path} failed: {ex.Message}");
            _retryAfter[info.Path] = Environment.TickCount64 + (long)RetryDelay.TotalMilliseconds;
        }
        finally
        {
            _connecting.TryRemove(info.Path, out _);
        }
    }

    private void OnDeviceDisconnected(WiimoteDevice dev)
    {
        dev.Disconnected -= OnDeviceDisconnected;
        if (!_attachedPaths.TryRemove(dev.DevicePath, out _)) return;

        ControllerSlot? slot;
        lock (_slotsLock) slot = _slots.FirstOrDefault(s => s.Device == dev);
        slot?.Detach(dev);
        dev.Dispose();
        _retryAfter[dev.DevicePath] = Environment.TickCount64 + 1000;
        if (slot is null) return;
        if (slot.TurnedOff)
            Log.Info($"{slot.Config.Name} turned off. Press any button on the Wiimote to turn it on again.");
        else
            Log.Info($"{slot.Config.Name} lost. It reconnects automatically: press any button on the Wiimote.");
    }

    // ------------------------------------------------------------------ turning off

    /// <summary>
    /// Switches a Wiimote off by dropping its Bluetooth connection. The pairing stays, so a button
    /// press turns it on and reconnects it. Blocking; call from a background thread.
    /// </summary>
    public bool TurnOff(ControllerSlot slot, out string message, string reason = "")
    {
        var dev = slot.Device;
        if (dev is null || !dev.IsConnected)
        {
            message = $"{slot.Config.Name} is already off.";
            return true;
        }

        ulong? address = ParseAddress(slot.Id);
        if (address is null)
        {
            message = $"{slot.Config.Name}: Windows did not report its Bluetooth address, so it can't be turned off from here.";
            Log.Warn(message);
            return false;
        }

        if (!_turningOff.TryAdd(slot.Id, 0))
        {
            message = $"{slot.Config.Name} is already turning off.";
            return true;
        }

        try
        {
            Log.Info($"Turning off {slot.Config.Name}{(reason.Length > 0 ? $" ({reason})" : "")}…");
            dev.SetRumble(false);
            if (!Bluetooth.WiimoteBluetooth.Disconnect(address.Value, out message))
            {
                Log.Error($"{slot.Config.Name}: {message}");
                return false;
            }

            slot.MarkTurnedOff();
            // Don't wait for the read timeout: detach right away.
            OnDeviceDisconnected(dev);
            message = $"{slot.Config.Name} turned off.";
            return true;
        }
        finally
        {
            _turningOff.TryRemove(slot.Id, out _);
        }
    }

    /// <summary>Turns off every connected Wiimote. Returns how many failed.</summary>
    public int TurnOffAll(out string? firstError)
    {
        firstError = null;
        int failed = 0;
        foreach (var slot in Slots.Where(s => s.IsConnected))
        {
            if (TurnOff(slot, out var msg)) continue;
            failed++;
            firstError ??= msg;
        }
        return failed;
    }

    private void CheckIdle()
    {
        int minutes = _config.AutoOffMinutes;
        if (minutes <= 0) return;
        var limit = TimeSpan.FromMinutes(minutes);
        long now = Environment.TickCount64;

        foreach (var slot in Slots)
        {
            if (!slot.IsConnected || slot.IdleTime < limit || _turningOff.ContainsKey(slot.Id)) continue;
            if (_autoOffBlockedUntil.TryGetValue(slot.Id, out long until) && now < until) continue;

            _ = Task.Run(() =>
            {
                if (!TurnOff(slot, out _, $"idle for {minutes} min"))
                    _autoOffBlockedUntil[slot.Id] = Environment.TickCount64 + 5 * 60_000; // don't spam retries
            });
        }
    }

    private void OnLowBattery(ControllerSlot slot, int percent)
    {
        Log.Warn($"{slot.Config.Name}: battery low ({percent}%).");
        if (_config.LowBatteryRumble) _ = slot.LowBatteryRumbleAsync();
        LowBattery?.Invoke(slot, percent);
    }

    private static ulong? ParseAddress(string id)
    {
        string? mac = Bluetooth.BtAddress.Normalize(id);
        return mac is null ? null : Convert.ToUInt64(mac.Replace(":", ""), 16);
    }

    private ControllerSlot? FindSlot(string? serial, string path)
    {
        string id = Bluetooth.BtAddress.Normalize(serial) ?? path.ToLowerInvariant();
        lock (_slotsLock)
            return _slots.FirstOrDefault(s => s.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
    }

    private int NextFreePlayerIndex()
    {
        lock (_slotsLock)
        {
            var used = _slots.Select(s => s.Config.PlayerIndex).ToHashSet();
            for (int i = 1; ; i++)
                if (!used.Contains(i)) return i;
        }
    }

    private ControllerSlot CreateSlot(ControllerConfig cfg)
    {
        var slot = new ControllerSlot(cfg, PadFactory);
        slot.Changed += _ => RequestSave();
        slot.LowBattery += OnLowBattery;
        return slot;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _timer?.Dispose();
        foreach (var slot in Slots)
        {
            var dev = slot.Device;
            slot.Dispose();
            dev?.Dispose();
        }
        foreach (var dev in _attachedPaths.Values) dev.Dispose();
        _attachedPaths.Clear();
        SaveNow();
    }
}

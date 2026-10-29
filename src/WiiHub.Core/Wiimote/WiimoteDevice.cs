using System.Threading.Channels;
using WiiHub.Core.Bluetooth;
using WiiHub.Core.Hid;
using static WiiHub.Core.Wiimote.WiimoteProtocol;

namespace WiiHub.Core.Wiimote;

/// <summary>
/// One connected Wiimote: owns the HID handle, a reader thread that parses input reports,
/// and a command worker that runs request/response sequences (memory reads, extension init).
/// </summary>
public sealed class WiimoteDevice : IDisposable
{
    private const int ReadTimeoutMs = 200;
    private const int ProbeAfterMs = 1500;
    private const int DisconnectAfterMs = 3500;
    private static readonly TimeSpan BatteryPollInterval = TimeSpan.FromSeconds(60);

    private readonly HidDevice _hid;
    private readonly Channel<Func<Task>> _commands = Channel.CreateUnbounded<Func<Task>>(
        new UnboundedChannelOptions { SingleReader = true });
    private readonly SemaphoreSlim _memLock = new(1, 1);
    private readonly object _stateLock = new();
    private readonly CancellationTokenSource _cts = new();

    private Thread? _reader;
    private Task? _worker;
    private WiimoteState _state = new();
    private volatile bool _rumble;
    private byte _ledMask;
    private int _disconnected;
    private volatile bool _online; // passed the "does it answer" check
    private volatile bool _extensionReady;
    private volatile bool _extensionPlugged;
    private PendingRead? _pendingRead;
    private TaskCompletionSource<int>? _pendingWriteAck;
    private TaskCompletionSource<bool>? _statusWaiter;

    /// <summary>Stable identity: the Bluetooth MAC when Windows exposes it, else the device path.</summary>
    public string Id { get; }
    public string? MacAddress { get; }
    public string DevicePath => _hid.Path;
    public WiimoteModel Model { get; }
    public AccelCalibration Calibration { get; private set; } = AccelCalibration.Default;

    public WiimoteState State
    {
        get { lock (_stateLock) return _state; }
    }

    public bool IsConnected => Volatile.Read(ref _disconnected) == 0;

    /// <summary>Raised on the reader thread for every input report (≈100 Hz in continuous mode).</summary>
    public event Action<WiimoteDevice, WiimoteState>? StateChanged;

    /// <summary>Raised once, from a background thread, when the link is lost.</summary>
    public event Action<WiimoteDevice>? Disconnected;

    /// <summary>Raised when an extension (guitar, nunchuk...) is plugged/unplugged and identified.</summary>
    public event Action<WiimoteDevice, ExtensionType>? ExtensionChanged;

    private WiimoteDevice(HidDevice hid, HidDeviceInfo info)
    {
        _hid = hid;
        MacAddress = BtAddress.Normalize(info.Serial);
        Id = MacAddress ?? info.Path.ToLowerInvariant();
        Model = info.ProductId == ProductIdMotionPlusInside ? WiimoteModel.MotionPlusInside : WiimoteModel.Original;
    }

    // ------------------------------------------------------------------ connect

    /// <summary>
    /// Opens the HID device and checks the Wiimote actually answers. Windows keeps HID nodes of
    /// paired-but-sleeping Wiimotes around, so a successful open alone means nothing.
    /// </summary>
    public static async Task<WiimoteDevice?> ConnectAsync(HidDeviceInfo info, int playerIndex)
    {
        var hid = HidDevice.Open(info.Path);
        if (hid is null) return null;

        var dev = new WiimoteDevice(hid, info);
        dev._ledMask = LedMask(playerIndex);
        dev.StartReader();

        bool alive = await dev.RequestStatusAsync(1200).ConfigureAwait(false);
        if (!alive)
        {
            dev.Dispose();
            return null;
        }

        dev._online = true;
        dev._worker = Task.Run(dev.CommandLoopAsync);
        dev.Enqueue(dev.InitializeAsync);
        return dev;
    }

    private async Task InitializeAsync()
    {
        Send(BuildLeds(_ledMask, rumble: false));

        var calData = await ReadMemoryAsync(EepromAccelCalibration, 10, registers: false).ConfigureAwait(false);
        if (calData is not null && ParseAccelCalibration(calData) is { } cal)
            Calibration = cal;

        // Short "hello" rumble so the player knows which controller just connected.
        SetRumble(true);
        await Task.Delay(180).ConfigureAwait(false);
        SetRumble(false);

        // The status report that proved the Wiimote alive may already have queued an extension
        // init; make sure reporting is on in any case.
        SetReportMode();
        Log.Info($"Wiimote {Id} ready ({Model}).");
    }

    // ------------------------------------------------------------------ public commands

    public void SetPlayerLeds(int playerIndex)
    {
        _ledMask = LedMask(playerIndex);
        Send(BuildLeds(_ledMask, _rumble));
    }

    public void SetRumble(bool on)
    {
        if (_rumble == on) return;
        _rumble = on;
        Send(BuildRumble(on));
    }

    public void RequestStatus() => Send(BuildStatusRequest(_rumble));

    // ------------------------------------------------------------------ reader thread

    private void StartReader()
    {
        _reader = new Thread(ReaderLoop)
        {
            IsBackground = true,
            Name = "Wiimote reader",
            Priority = ThreadPriority.AboveNormal,
        };
        _reader.Start();
    }

    private void ReaderLoop()
    {
        var buffer = new byte[Math.Max(_hid.InputReportLength, 22)];
        var lastReport = Environment.TickCount64;
        var lastBatteryPoll = Environment.TickCount64;
        bool probeSent = false;

        while (!_cts.IsCancellationRequested)
        {
            var r = _hid.Read(buffer, ReadTimeoutMs, out int n);
            long now = Environment.TickCount64;

            if (r == HidIoResult.Ok && n > 0)
            {
                lastReport = now;
                probeSent = false;
                try { HandleReport(buffer.AsSpan(0, n)); }
                catch (Exception ex) { Log.Error($"Wiimote {Id}: bad report 0x{buffer[0]:X2}: {ex.Message}"); }

                if (now - lastBatteryPoll > BatteryPollInterval.TotalMilliseconds)
                {
                    lastBatteryPoll = now;
                    RequestStatus();
                }
                continue;
            }

            if (_cts.IsCancellationRequested) break;

            if (r is HidIoResult.Disconnected or HidIoResult.Error)
            {
                MarkDisconnected($"read failed ({r})");
                break;
            }

            // Timeout. In continuous mode silence means trouble: probe first, then give up.
            long silent = now - lastReport;
            if (silent > DisconnectAfterMs)
            {
                MarkDisconnected("no reports");
                break;
            }
            if (silent > ProbeAfterMs && !probeSent)
            {
                probeSent = true;
                RequestStatus();
            }
        }
    }

    private void HandleReport(ReadOnlySpan<byte> report)
    {
        byte id = report[0];
        WiimoteButtons buttons = HasCoreButtons(id) && report.Length >= 3 ? ParseButtons(report) : State.Buttons;

        switch (id)
        {
            case InStatus:
            {
                var status = ParseStatus(report);
                UpdateState(s => s with
                {
                    Buttons = buttons,
                    BatteryPercent = status.BatteryPercent,
                    BatteryLow = status.BatteryLow,
                });
                _statusWaiter?.TrySetResult(true);

                // Reporting stops after every status report until the mode is set again.
                bool plugged = status.ExtensionConnected;
                if (plugged != _extensionPlugged || (plugged && !_extensionReady))
                {
                    _extensionPlugged = plugged;
                    _extensionReady = false;
                    Enqueue(() => HandleExtensionChangeAsync(plugged));
                }
                else
                {
                    Enqueue(() => { SetReportMode(); return Task.CompletedTask; });
                }
                return;
            }

            case InReadData:
            {
                UpdateState(s => s with { Buttons = buttons });
                var pending = _pendingRead;
                if (pending is null) return;
                int error = report[3] & 0x0F;
                int size = (report[3] >> 4) + 1;
                if (error != 0)
                {
                    pending.Completion.TrySetResult(null);
                    return;
                }
                pending.Append(report.Slice(6, Math.Min(size, report.Length - 6)));
                return;
            }

            case InAck:
            {
                UpdateState(s => s with { Buttons = buttons });
                if (report[3] == OutWriteMemory) _pendingWriteAck?.TrySetResult(report[4]);
                return;
            }
        }

        if (id < 0x30 || id > 0x3F) return;

        bool hasAccel = TryParseAccel(report, out var accel);
        var ext = GetExtensionBytes(report);

        WiimoteState newState;
        lock (_stateLock)
        {
            var s = _state with { Buttons = buttons };
            if (hasAccel) s = s with { Accel = accel, HasAccel = true };

            if (_extensionReady && ext.Length >= 6 && !IsAllFF(ext[..6]))
            {
                s = s.Extension switch
                {
                    ExtensionType.Guitar => s with { Guitar = ParseGuitar(ext) },
                    ExtensionType.Nunchuk => s with { Nunchuk = ParseNunchuk(ext) },
                    _ => s,
                };
            }
            _state = s;
            newState = s;
        }
        StateChanged?.Invoke(this, newState);
    }

    private static bool IsAllFF(ReadOnlySpan<byte> b)
    {
        foreach (var x in b) if (x != 0xFF) return false;
        return true;
    }

    private void UpdateState(Func<WiimoteState, WiimoteState> change)
    {
        WiimoteState s;
        lock (_stateLock)
        {
            _state = change(_state);
            s = _state;
        }
        StateChanged?.Invoke(this, s);
    }

    // ------------------------------------------------------------------ command worker

    private void Enqueue(Func<Task> command) => _commands.Writer.TryWrite(command);

    private async Task CommandLoopAsync()
    {
        try
        {
            await foreach (var cmd in _commands.Reader.ReadAllAsync(_cts.Token).ConfigureAwait(false))
            {
                try { await cmd().ConfigureAwait(false); }
                catch (Exception ex) { Log.Error($"Wiimote {Id}: command failed: {ex.Message}"); }
            }
        }
        catch (OperationCanceledException) { }
    }

    private async Task HandleExtensionChangeAsync(bool plugged)
    {
        if (!plugged)
        {
            UpdateState(s => s with { Extension = ExtensionType.None, Guitar = null, Nunchuk = null });
            _extensionReady = false;
            SetReportMode();
            Log.Info($"Wiimote {Id}: extension removed.");
            ExtensionChanged?.Invoke(this, ExtensionType.None);
            return;
        }

        // Give the extension a moment to power up after being plugged in.
        await Task.Delay(150).ConfigureAwait(false);

        ExtensionType type = ExtensionType.Unknown;
        for (int attempt = 1; attempt <= 3 && !_cts.IsCancellationRequested; attempt++)
        {
            // "New" init: disables encryption; works for Nintendo and third-party extensions.
            await WriteRegisterAsync(RegExtInit1, 0x55).ConfigureAwait(false);
            await WriteRegisterAsync(RegExtInit2, 0x00).ConfigureAwait(false);

            var id = await ReadMemoryAsync(RegExtId, 6, registers: true).ConfigureAwait(false);
            if (id is not null)
            {
                type = IdentifyExtension(id);
                Log.Info($"Wiimote {Id}: extension id {Convert.ToHexString(id)} → {type}");
                if (type != ExtensionType.Unknown) break;
            }
            await Task.Delay(200).ConfigureAwait(false);
        }

        // Plugged state may have changed while we were initialising.
        if (!_extensionPlugged) return;

        UpdateState(s => s with { Extension = type, Guitar = null, Nunchuk = null });
        _extensionReady = true;
        SetReportMode();
        ExtensionChanged?.Invoke(this, type);
    }

    private void SetReportMode()
    {
        byte mode = _extensionPlugged && _extensionReady ? ModeButtonsAccelExt16 : ModeButtonsAccel;
        Send(BuildReportMode(mode, continuous: true, _rumble));
    }

    private async Task<bool> RequestStatusAsync(int timeoutMs)
    {
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _statusWaiter = tcs;
        try
        {
            if (!Send(BuildStatusRequest(_rumble))) return false;
            var done = await Task.WhenAny(tcs.Task, Task.Delay(timeoutMs)).ConfigureAwait(false);
            return done == tcs.Task;
        }
        finally { _statusWaiter = null; }
    }

    private async Task<bool> WriteRegisterAsync(int address, byte value)
    {
        await _memLock.WaitAsync().ConfigureAwait(false);
        var tcs = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pendingWriteAck = tcs;
        try
        {
            if (!Send(BuildWriteMemory(address, [value], registers: true, _rumble))) return false;
            var done = await Task.WhenAny(tcs.Task, Task.Delay(500)).ConfigureAwait(false);
            if (done != tcs.Task) return false;
            return tcs.Task.Result == 0;
        }
        finally
        {
            _pendingWriteAck = null;
            _memLock.Release();
        }
    }

    private async Task<byte[]?> ReadMemoryAsync(int address, int size, bool registers)
    {
        await _memLock.WaitAsync().ConfigureAwait(false);
        var pending = new PendingRead(size);
        _pendingRead = pending;
        try
        {
            if (!Send(BuildReadMemory(address, size, registers, _rumble))) return null;
            var done = await Task.WhenAny(pending.Completion.Task, Task.Delay(1000)).ConfigureAwait(false);
            return done == pending.Completion.Task ? pending.Completion.Task.Result : null;
        }
        finally
        {
            _pendingRead = null;
            _memLock.Release();
        }
    }

    private sealed class PendingRead(int size)
    {
        private readonly byte[] _data = new byte[size];
        private int _received;

        public TaskCompletionSource<byte[]?> Completion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void Append(ReadOnlySpan<byte> chunk)
        {
            int n = Math.Min(chunk.Length, _data.Length - _received);
            chunk[..n].CopyTo(_data.AsSpan(_received));
            _received += n;
            if (_received >= _data.Length) Completion.TrySetResult(_data);
        }
    }

    // ------------------------------------------------------------------ io / lifetime

    private bool Send(byte[] report)
    {
        if (!IsConnected) return false;
        var r = _hid.Write(report);
        if (r == HidIoResult.Disconnected)
        {
            MarkDisconnected("write failed");
            return false;
        }
        return r == HidIoResult.Ok;
    }

    private void MarkDisconnected(string reason)
    {
        if (Interlocked.Exchange(ref _disconnected, 1) != 0) return;
        // Sleeping paired Wiimotes fail the connect probe every few seconds; keep that out of the log.
        if (_online) Log.Info($"Wiimote {Id} disconnected ({reason}).");
        else Log.Debug($"Wiimote {Id} not answering ({reason}).");
        _statusWaiter?.TrySetResult(false);
        ThreadPool.QueueUserWorkItem(_ => Disconnected?.Invoke(this));
    }

    public void Dispose()
    {
        if (IsConnected && _rumble)
        {
            try { _hid.Write(BuildRumble(false), 200); } catch { /* best effort */ }
        }
        Interlocked.Exchange(ref _disconnected, 1);
        if (_cts.IsCancellationRequested) return;
        _cts.Cancel();
        _commands.Writer.TryComplete();
        _hid.CancelPendingIo();
        if (_reader is not null && _reader != Thread.CurrentThread) _reader.Join(1000);
        _hid.Dispose();
        _cts.Dispose();
    }
}

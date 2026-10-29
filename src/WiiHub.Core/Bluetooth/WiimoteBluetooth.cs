using System.Runtime.InteropServices;
using WiiHub.Core.Native;
using static WiiHub.Core.Native.BluetoothApi;

namespace WiiHub.Core.Bluetooth;

public enum PairMode
{
    /// <summary>
    /// Red SYNC button. PIN = the PC's Bluetooth address. The Wiimote remembers the PC and
    /// reconnects by itself when any button is pressed (e.g. after swapping batteries).
    /// </summary>
    Permanent,

    /// <summary>
    /// 1+2 buttons. PIN = the Wiimote's own address. Works, but the Wiimote forgets the PC as
    /// soon as it powers off, so it has to be paired again every time.
    /// </summary>
    Temporary,
}

public sealed record BluetoothWiimoteInfo(
    ulong Address,
    string Name,
    bool Connected,
    bool Remembered,
    bool Authenticated)
{
    public string AddressText => BtAddress.Format(Address);
}

public sealed record PairResult(ulong Address, string Name, bool Success, string Message);

/// <summary>
/// Pairs Wiimotes through the Microsoft Bluetooth stack (bthprops.cpl) and manages the
/// remembered devices. Every call is blocking; call from a background thread.
/// </summary>
public static unsafe class WiimoteBluetooth
{
    /// <summary>Class of Device reported by Wiimotes (Peripheral / Joystick).</summary>
    private const uint WiimoteClassOfDevice = 0x002504;

    public static bool IsWiimoteName(string? name) =>
        name is not null &&
        (name.Equals("Nintendo RVL-CNT-01", StringComparison.OrdinalIgnoreCase) ||
         name.Equals("Nintendo RVL-CNT-01-TR", StringComparison.OrdinalIgnoreCase));

    private static bool LooksLikeWiimote(in BLUETOOTH_DEVICE_INFO info) =>
        IsWiimoteName(info.szName) ||
        (string.IsNullOrEmpty(info.szName) && (info.ulClassofDevice & 0x1FFFFF) == WiimoteClassOfDevice);

    /// <summary>Returns true when at least one Bluetooth radio is present and the API is usable.</summary>
    public static bool IsBluetoothAvailable(out string message)
    {
        try
        {
            var radios = OpenRadios();
            try
            {
                if (radios.Count == 0)
                {
                    message = "No Bluetooth adapter found (or it is turned off).";
                    return false;
                }
                message = $"Bluetooth adapter: {radios[0].Name} ({BtAddress.Format(radios[0].Address)})";
                return true;
            }
            finally { CloseRadios(radios); }
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            message = "The Windows Bluetooth API (bthprops.cpl) is not available.";
            return false;
        }
    }

    /// <summary>Wiimotes Windows already knows about (paired / remembered), no radio inquiry.</summary>
    public static List<BluetoothWiimoteInfo> GetKnownWiimotes()
    {
        var result = new List<BluetoothWiimoteInfo>();
        var radios = OpenRadios();
        try
        {
            foreach (var radio in radios)
            {
                foreach (var d in FindDevices(radio.Handle, issueInquiry: false, timeoutMultiplier: 0))
                {
                    if (!LooksLikeWiimote(d)) continue;
                    if (d.fRemembered == 0 && d.fAuthenticated == 0 && d.fConnected == 0) continue;
                    if (result.Any(r => r.Address == d.Address)) continue;
                    result.Add(ToInfo(d));
                }
            }
        }
        finally { CloseRadios(radios); }
        return result;
    }

    /// <summary>
    /// Searches for Wiimotes that are in pairing mode and pairs every one it finds.
    /// Wiimotes stay discoverable for ~20 seconds after pressing SYNC (or 1+2).
    /// </summary>
    public static List<PairResult> PairDiscoverable(PairMode mode, Action<string> progress, CancellationToken ct,
        int maxRounds = 4)
    {
        var results = new List<PairResult>();
        var skipped = new HashSet<ulong>();
        var radios = OpenRadios();
        try
        {
            if (radios.Count == 0)
            {
                progress("No Bluetooth adapter found. Make sure Bluetooth is turned on.");
                return results;
            }

            for (int round = 1; round <= maxRounds && !ct.IsCancellationRequested; round++)
            {
                foreach (var radio in radios)
                {
                    if (ct.IsCancellationRequested) break;
                    progress($"Searching for Wiimotes… (round {round}/{maxRounds})");

                    // ~1.28 s per unit; 5 units ≈ 6.4 s inquiry.
                    foreach (var device in FindDevices(radio.Handle, issueInquiry: true, timeoutMultiplier: 5))
                    {
                        if (ct.IsCancellationRequested) break;
                        if (!LooksLikeWiimote(device)) continue;
                        if (results.Any(r => r.Address == device.Address && r.Success)) continue;

                        // Already paired and connected: nothing to do.
                        if (device.fConnected != 0 && device.fAuthenticated != 0)
                        {
                            progress($"{BtAddress.Format(device.Address)} is already connected.");
                            continue;
                        }

                        // A remembered-but-authenticated device can't be told apart from one that is
                        // merely in range, so we only pair devices Windows does not have yet, plus
                        // half-finished pairings (remembered but never authenticated).
                        if (device.fRemembered != 0 && device.fAuthenticated != 0)
                        {
                            if (skipped.Add(device.Address))
                                progress($"Skipped {BtAddress.Format(device.Address)}: Windows already has it paired. " +
                                         "If this is the Wiimote you are syncing, remove it from the list first.");
                            continue;
                        }

                        var dev = device;
                        if (dev.fRemembered != 0)
                        {
                            ulong addr = dev.Address;
                            int rm = BluetoothRemoveDevice(ref addr);
                            progress($"Removed stale pairing for {BtAddress.Format(dev.Address)} (0x{rm:X}).");
                        }

                        results.Add(PairOne(radio, ref dev, mode, progress));
                    }
                }

                if (results.Any(r => r.Success))
                {
                    // Give a second Wiimote a chance if the user is syncing several at once.
                    if (round < maxRounds) progress("Paired! Searching once more for other Wiimotes in SYNC mode…");
                    maxRounds = Math.Min(maxRounds, round + 1);
                }
            }
        }
        finally { CloseRadios(radios); }

        if (results.Count == 0 && !ct.IsCancellationRequested)
            progress("No Wiimote in pairing mode was found. Press the red SYNC button (under the battery cover) and try again.");
        return results;
    }

    private static PairResult PairOne(Radio radio, ref BLUETOOTH_DEVICE_INFO device, PairMode mode, Action<string> progress)
    {
        string addrText = BtAddress.Format(device.Address);
        string name = string.IsNullOrEmpty(device.szName) ? "Wiimote" : device.szName;
        progress($"Found {name} at {addrText}, pairing…");

        // Permanent: PIN = host (PC) address; Temporary (1+2): PIN = Wiimote address.
        byte[] pin = BtAddress.ToPinBytes(mode == PairMode.Permanent ? radio.Address : device.Address);
        char* pass = stackalloc char[6];
        for (int i = 0; i < 6; i++) pass[i] = (char)pin[i];

        int auth = BluetoothAuthenticateDevice(IntPtr.Zero, radio.Handle, ref device, pass, 6);
        if (auth != 0)
        {
            // Windows sometimes reports an error even though pairing worked; continue anyway, the
            // HID service step below is what actually matters.
            progress($"  Authentication returned 0x{auth:X} ({DescribeError(auth)}), continuing.");
        }
        else
        {
            progress("  Authenticated.");
        }

        Guid hid = HidServiceClass;
        int svc = BluetoothSetServiceState(radio.Handle, ref device, ref hid, BLUETOOTH_SERVICE_ENABLE);
        if (svc != 0)
        {
            progress($"  Enabling the HID service failed: 0x{svc:X} ({DescribeError(svc)}).");
            return new PairResult(device.Address, name, false, $"HID service error 0x{svc:X}");
        }

        progress(mode == PairMode.Permanent
            ? $"  {addrText} paired. It will reconnect by itself when you press a button."
            : $"  {addrText} paired (temporary).");
        return new PairResult(device.Address, name, true, "Paired");
    }

    /// <summary>Re-enables the HID service of a known Wiimote. Fixes "paired but no input".</summary>
    public static bool RepairHidService(ulong address, Action<string> progress)
    {
        var radios = OpenRadios();
        try
        {
            foreach (var radio in radios)
            {
                foreach (var d in FindDevices(radio.Handle, issueInquiry: false, timeoutMultiplier: 0))
                {
                    if (d.Address != address) continue;
                    var dev = d;
                    Guid hid = HidServiceClass;
                    int r = BluetoothSetServiceState(radio.Handle, ref dev, ref hid, BLUETOOTH_SERVICE_ENABLE);
                    progress(r == 0
                        ? $"HID service re-enabled for {BtAddress.Format(address)}."
                        : $"Could not re-enable HID for {BtAddress.Format(address)}: 0x{r:X} ({DescribeError(r)}).");
                    return r == 0;
                }
            }
            progress($"{BtAddress.Format(address)} is not known to Windows. Pair it again.");
            return false;
        }
        finally { CloseRadios(radios); }
    }

    /// <summary>
    /// Drops the Bluetooth connection to a device without unpairing it. A Wiimote switches itself
    /// off when its connection is dropped, and (when paired with SYNC) reconnects on a button press.
    /// </summary>
    public static bool Disconnect(ulong address, out string message)
    {
        var radios = OpenRadios();
        try
        {
            if (radios.Count == 0)
            {
                message = "No Bluetooth adapter found.";
                return false;
            }

            int lastError = 0;
            foreach (var radio in radios)
            {
                ulong addr = address;
                if (DeviceIoControl(radio.Handle, IOCTL_BTH_DISCONNECT_DEVICE, ref addr, sizeof(ulong),
                        IntPtr.Zero, 0, out _, IntPtr.Zero))
                {
                    message = $"Disconnected {BtAddress.Format(address)}.";
                    return true;
                }
                lastError = Marshal.GetLastWin32Error();
            }

            message = lastError switch
            {
                5 => "Windows refused to disconnect the Wiimote (access denied). Run WiiHub as administrator to use “Turn off”.",
                1167 => "The Wiimote is already disconnected.",
                _ => $"Could not disconnect the Wiimote: {DescribeError(lastError)} (0x{lastError:X}).",
            };
            return lastError == 1167; // already off counts as success
        }
        finally { CloseRadios(radios); }
    }

    /// <summary>Removes (unpairs) a device from Windows.</summary>
    public static bool Remove(ulong address, Action<string> progress)
    {
        ulong a = address;
        int r = BluetoothRemoveDevice(ref a);
        progress(r == 0
            ? $"Removed {BtAddress.Format(address)} from Windows."
            : $"Removing {BtAddress.Format(address)} failed: 0x{r:X} ({DescribeError(r)}).");
        return r == 0;
    }

    // ------------------------------------------------------------------ helpers

    private sealed record Radio(IntPtr Handle, ulong Address, string Name);

    private static List<Radio> OpenRadios()
    {
        var list = new List<Radio>();
        var p = new BLUETOOTH_FIND_RADIO_PARAMS { dwSize = Marshal.SizeOf<BLUETOOTH_FIND_RADIO_PARAMS>() };
        IntPtr find = BluetoothFindFirstRadio(ref p, out IntPtr radio);
        if (find == IntPtr.Zero) return list;
        try
        {
            do
            {
                var info = new BLUETOOTH_RADIO_INFO
                {
                    dwSize = Marshal.SizeOf<BLUETOOTH_RADIO_INFO>(),
                    szName = string.Empty,
                };
                if (BluetoothGetRadioInfo(radio, ref info) == 0)
                    list.Add(new Radio(radio, info.address, info.szName ?? "Bluetooth"));
                else
                    Win32.CloseHandle(radio);
            } while (BluetoothFindNextRadio(find, out radio));
        }
        finally { BluetoothFindRadioClose(find); }
        return list;
    }

    private static void CloseRadios(List<Radio> radios)
    {
        foreach (var r in radios) Win32.CloseHandle(r.Handle);
    }

    private static List<BLUETOOTH_DEVICE_INFO> FindDevices(IntPtr radio, bool issueInquiry, byte timeoutMultiplier)
    {
        var list = new List<BLUETOOTH_DEVICE_INFO>();
        var search = new BLUETOOTH_DEVICE_SEARCH_PARAMS
        {
            dwSize = Marshal.SizeOf<BLUETOOTH_DEVICE_SEARCH_PARAMS>(),
            fReturnAuthenticated = 1,
            fReturnRemembered = 1,
            fReturnUnknown = 1,
            fReturnConnected = 1,
            fIssueInquiry = issueInquiry ? 1 : 0,
            cTimeoutMultiplier = timeoutMultiplier,
            hRadio = radio,
        };
        var info = NewDeviceInfo();
        IntPtr find = BluetoothFindFirstDevice(ref search, ref info);
        if (find == IntPtr.Zero) return list;
        try
        {
            do
            {
                list.Add(info);
                info = NewDeviceInfo();
            } while (BluetoothFindNextDevice(find, ref info));
        }
        finally { BluetoothFindDeviceClose(find); }
        return list;
    }

    private static BluetoothWiimoteInfo ToInfo(in BLUETOOTH_DEVICE_INFO d) => new(
        d.Address,
        string.IsNullOrEmpty(d.szName) ? "Wiimote" : d.szName,
        d.fConnected != 0,
        d.fRemembered != 0,
        d.fAuthenticated != 0);

    private static string DescribeError(int code) => code switch
    {
        0 => "OK",
        5 => "access denied",
        87 => "invalid parameter",
        259 => "no more items",
        1167 => "device not connected",
        1168 => "not found",
        1223 => "cancelled",
        1244 => "not authenticated",
        _ => new System.ComponentModel.Win32Exception(code).Message,
    };
}

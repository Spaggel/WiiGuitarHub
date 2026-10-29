using System.Runtime.InteropServices;

namespace WiiHub.Core.Native;

/// <summary>
/// Microsoft Bluetooth stack (BluetoothAPIs) declarations. Exported by bthprops.cpl on all
/// supported Windows versions.
/// </summary>
internal static unsafe class BluetoothApi
{
    private const string Dll = "bthprops.cpl";

    public const uint BLUETOOTH_SERVICE_DISABLE = 0x00;
    public const uint BLUETOOTH_SERVICE_ENABLE = 0x01;

    /// <summary>HumanInterfaceDeviceServiceClass_UUID.</summary>
    public static readonly Guid HidServiceClass = new("00001124-0000-1000-8000-00805F9B34FB");

    [StructLayout(LayoutKind.Sequential)]
    public struct SYSTEMTIME
    {
        public ushort wYear, wMonth, wDayOfWeek, wDay, wHour, wMinute, wSecond, wMilliseconds;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct BLUETOOTH_FIND_RADIO_PARAMS
    {
        public int dwSize;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct BLUETOOTH_RADIO_INFO
    {
        public int dwSize;
        public ulong address;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 248)]
        public string szName;
        public uint ulClassofDevice;
        public ushort lmpSubversion;
        public ushort manufacturer;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct BLUETOOTH_DEVICE_INFO
    {
        public int dwSize;
        public ulong Address;
        public uint ulClassofDevice;
        public int fConnected;
        public int fRemembered;
        public int fAuthenticated;
        public SYSTEMTIME stLastSeen;
        public SYSTEMTIME stLastUsed;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 248)]
        public string szName;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct BLUETOOTH_DEVICE_SEARCH_PARAMS
    {
        public int dwSize;
        public int fReturnAuthenticated;
        public int fReturnRemembered;
        public int fReturnUnknown;
        public int fReturnConnected;
        public int fIssueInquiry;
        public byte cTimeoutMultiplier;
        public IntPtr hRadio;
    }

    [DllImport(Dll, SetLastError = true)]
    public static extern IntPtr BluetoothFindFirstRadio(ref BLUETOOTH_FIND_RADIO_PARAMS findParams, out IntPtr radio);

    [DllImport(Dll, SetLastError = true)]
    public static extern bool BluetoothFindNextRadio(IntPtr find, out IntPtr radio);

    [DllImport(Dll, SetLastError = true)]
    public static extern bool BluetoothFindRadioClose(IntPtr find);

    [DllImport(Dll, SetLastError = true)]
    public static extern int BluetoothGetRadioInfo(IntPtr radio, ref BLUETOOTH_RADIO_INFO radioInfo);

    [DllImport(Dll, SetLastError = true)]
    public static extern IntPtr BluetoothFindFirstDevice(ref BLUETOOTH_DEVICE_SEARCH_PARAMS searchParams,
        ref BLUETOOTH_DEVICE_INFO deviceInfo);

    [DllImport(Dll, SetLastError = true)]
    public static extern bool BluetoothFindNextDevice(IntPtr find, ref BLUETOOTH_DEVICE_INFO deviceInfo);

    [DllImport(Dll, SetLastError = true)]
    public static extern bool BluetoothFindDeviceClose(IntPtr find);

    /// <summary>
    /// Legacy PIN authentication. The Wiimote PIN is 6 raw bytes (a Bluetooth address), so the
    /// passkey is passed as a pointer to 6 WCHARs, each holding one byte.
    /// </summary>
    [DllImport(Dll, SetLastError = true)]
    public static extern int BluetoothAuthenticateDevice(IntPtr hwndParent, IntPtr radio,
        ref BLUETOOTH_DEVICE_INFO deviceInfo, char* passkey, int passkeyLength);

    [DllImport(Dll, SetLastError = true)]
    public static extern int BluetoothSetServiceState(IntPtr radio, ref BLUETOOTH_DEVICE_INFO deviceInfo,
        ref Guid serviceGuid, uint serviceFlags);

    [DllImport(Dll, SetLastError = true)]
    public static extern int BluetoothRemoveDevice(ref ulong address);

    /// <summary>
    /// IOCTL_BTH_DISCONNECT_DEVICE = CTL_CODE(FILE_DEVICE_BLUETOOTH (0x41), 0x03, METHOD_BUFFERED, FILE_ANY_ACCESS).
    /// Sent to a radio handle with the remote BTH_ADDR (ulong) as input. Drops the ACL link only;
    /// the pairing (link key) is kept.
    /// </summary>
    public const uint IOCTL_BTH_DISCONNECT_DEVICE = 0x0041000C;

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool DeviceIoControl(IntPtr device, uint ioControlCode, ref ulong inBuffer, int inBufferSize,
        IntPtr outBuffer, int outBufferSize, out int bytesReturned, IntPtr overlapped);

    public static BLUETOOTH_DEVICE_INFO NewDeviceInfo() => new()
    {
        dwSize = Marshal.SizeOf<BLUETOOTH_DEVICE_INFO>(),
        szName = string.Empty,
    };
}

using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;
using WiiHub.Core.Native;
using static WiiHub.Core.Native.Win32;

namespace WiiHub.Core.Hid;

public sealed record HidDeviceInfo(string Path, ushort VendorId, ushort ProductId, string? Serial);

public enum HidIoResult { Ok, Timeout, Disconnected, Error }

/// <summary>
/// Minimal overlapped-I/O HID device. One reader thread and one writer at a time
/// (writes are serialised internally).
/// </summary>
public sealed unsafe class HidDevice : IDisposable
{
    private readonly SafeFileHandle _handle;
    private readonly ManualResetEvent _readEvent = new(false);
    private readonly ManualResetEvent _writeEvent = new(false);
    private readonly NativeOverlapped* _readOv;
    private readonly NativeOverlapped* _writeOv;
    private readonly byte[] _readBuffer;
    private readonly byte[] _writeBuffer;
    private readonly object _writeLock = new();
    private bool _useSetOutputReport;
    private volatile bool _disposed;

    public string Path { get; }
    public int InputReportLength { get; }
    public int OutputReportLength { get; }

    private HidDevice(string path, SafeFileHandle handle, int inLen, int outLen)
    {
        Path = path;
        _handle = handle;
        InputReportLength = inLen;
        OutputReportLength = outLen;
        _readBuffer = GC.AllocateArray<byte>(inLen, pinned: true);
        _writeBuffer = GC.AllocateArray<byte>(outLen, pinned: true);
        _readOv = (NativeOverlapped*)NativeMemory.AllocZeroed((nuint)sizeof(NativeOverlapped));
        _writeOv = (NativeOverlapped*)NativeMemory.AllocZeroed((nuint)sizeof(NativeOverlapped));
    }

    // ------------------------------------------------------------------ enumeration

    /// <summary>Lists present HID devices matching the vendor id and any of the product ids.</summary>
    public static List<HidDeviceInfo> Enumerate(ushort vendorId, params ushort[] productIds)
    {
        var result = new List<HidDeviceInfo>();
        HidD_GetHidGuid(out Guid hidGuid);
        IntPtr set = SetupDiGetClassDevs(ref hidGuid, null, IntPtr.Zero, DIGCF_PRESENT | DIGCF_DEVICEINTERFACE);
        if (set == IntPtr.Zero || set == new IntPtr(-1)) return result;
        try
        {
            var did = new SP_DEVICE_INTERFACE_DATA { cbSize = Marshal.SizeOf<SP_DEVICE_INTERFACE_DATA>() };
            for (uint i = 0; SetupDiEnumDeviceInterfaces(set, IntPtr.Zero, ref hidGuid, i, ref did); i++)
            {
                string? path = GetDevicePath(set, ref did);
                if (path is null) continue;

                // Opening with 0 access only queries attributes; it never disturbs the device.
                using var h = CreateFile(path, 0, FILE_SHARE_READ | FILE_SHARE_WRITE, IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);
                if (h.IsInvalid) continue;
                var attr = new HIDD_ATTRIBUTES { Size = Marshal.SizeOf<HIDD_ATTRIBUTES>() };
                if (!HidD_GetAttributes(h, ref attr)) continue;
                if (attr.VendorID != vendorId || Array.IndexOf(productIds, attr.ProductID) < 0) continue;

                result.Add(new HidDeviceInfo(path, attr.VendorID, attr.ProductID, GetSerial(h)));
            }
        }
        finally { SetupDiDestroyDeviceInfoList(set); }
        return result;
    }

    private static string? GetDevicePath(IntPtr set, ref SP_DEVICE_INTERFACE_DATA did)
    {
        SetupDiGetDeviceInterfaceDetail(set, ref did, IntPtr.Zero, 0, out uint required, IntPtr.Zero);
        if (required == 0) return null;
        IntPtr detail = Marshal.AllocHGlobal((int)required);
        try
        {
            // SP_DEVICE_INTERFACE_DETAIL_DATA_W.cbSize: 8 on x64, 6 on x86.
            Marshal.WriteInt32(detail, IntPtr.Size == 8 ? 8 : 6);
            if (!SetupDiGetDeviceInterfaceDetail(set, ref did, detail, required, out _, IntPtr.Zero)) return null;
            return Marshal.PtrToStringUni(detail + 4);
        }
        finally { Marshal.FreeHGlobal(detail); }
    }

    private static string? GetSerial(SafeFileHandle h)
    {
        byte* buf = stackalloc byte[256];
        new Span<byte>(buf, 256).Clear();
        if (!HidD_GetSerialNumberString(h, buf, 254)) return null;
        string s = new string((char*)buf, 0, 127);
        int nul = s.IndexOf('\0');
        if (nul >= 0) s = s[..nul];
        s = s.Trim();
        return s.Length == 0 ? null : s;
    }

    // ------------------------------------------------------------------ open / io

    public static HidDevice? Open(string path)
    {
        var handle = CreateFile(path, GENERIC_READ | GENERIC_WRITE, FILE_SHARE_READ | FILE_SHARE_WRITE,
            IntPtr.Zero, OPEN_EXISTING, FILE_FLAG_OVERLAPPED, IntPtr.Zero);
        if (handle.IsInvalid)
        {
            Log.Debug($"CreateFile failed ({Marshal.GetLastWin32Error()}) for {path}");
            handle.Dispose();
            return null;
        }

        int inLen = 22, outLen = 22; // Wiimote defaults
        if (HidD_GetPreparsedData(handle, out IntPtr pre))
        {
            try
            {
                if (HidP_GetCaps(pre, out HIDP_CAPS caps) == 0x00110000) // HIDP_STATUS_SUCCESS
                {
                    if (caps.InputReportByteLength > 0) inLen = caps.InputReportByteLength;
                    if (caps.OutputReportByteLength > 0) outLen = caps.OutputReportByteLength;
                }
            }
            finally { HidD_FreePreparsedData(pre); }
        }

        return new HidDevice(path, handle, Math.Max(inLen, 22), Math.Max(outLen, 22));
    }

    /// <summary>
    /// Reads one input report into <paramref name="destination"/> (report id at index 0).
    /// </summary>
    public HidIoResult Read(byte[] destination, int timeoutMs, out int length)
    {
        length = 0;
        if (_disposed) return HidIoResult.Disconnected;

        *_readOv = default;
        _readOv->EventHandle = _readEvent.SafeWaitHandle.DangerousGetHandle();
        _readEvent.Reset();

        fixed (byte* p = _readBuffer)
        {
            if (!ReadFile(_handle, p, _readBuffer.Length, IntPtr.Zero, _readOv))
            {
                int err = Marshal.GetLastWin32Error();
                if (err != ERROR_IO_PENDING) return Classify(err);
            }

            if (!_readEvent.WaitOne(timeoutMs))
            {
                CancelIoEx(_handle, _readOv);
                GetOverlappedResult(_handle, _readOv, out _, true); // wait for cancellation to finish
                return HidIoResult.Timeout;
            }

            if (!GetOverlappedResult(_handle, _readOv, out int n, false))
                return Classify(Marshal.GetLastWin32Error());

            n = Math.Min(n, destination.Length);
            Buffer.BlockCopy(_readBuffer, 0, destination, 0, n);
            length = n;
            return HidIoResult.Ok;
        }
    }

    /// <summary>Writes an output report (report id at index 0). Pads to the device's report length.</summary>
    public HidIoResult Write(ReadOnlySpan<byte> report, int timeoutMs = 500)
    {
        lock (_writeLock)
        {
            if (_disposed) return HidIoResult.Disconnected;
            Array.Clear(_writeBuffer);
            report[..Math.Min(report.Length, _writeBuffer.Length)].CopyTo(_writeBuffer);

            fixed (byte* p = _writeBuffer)
            {
                if (_useSetOutputReport)
                    return HidD_SetOutputReport(_handle, p, _writeBuffer.Length)
                        ? HidIoResult.Ok
                        : Classify(Marshal.GetLastWin32Error());

                *_writeOv = default;
                _writeOv->EventHandle = _writeEvent.SafeWaitHandle.DangerousGetHandle();
                _writeEvent.Reset();

                if (!WriteFile(_handle, p, _writeBuffer.Length, IntPtr.Zero, _writeOv))
                {
                    int err = Marshal.GetLastWin32Error();
                    if (err != ERROR_IO_PENDING)
                        return FallbackToSetOutputReport(p, err);
                }

                if (!_writeEvent.WaitOne(timeoutMs))
                {
                    CancelIoEx(_handle, _writeOv);
                    GetOverlappedResult(_handle, _writeOv, out _, true);
                    return HidIoResult.Timeout;
                }

                if (!GetOverlappedResult(_handle, _writeOv, out _, false))
                    return FallbackToSetOutputReport(p, Marshal.GetLastWin32Error());

                return HidIoResult.Ok;
            }
        }
    }

    private HidIoResult FallbackToSetOutputReport(byte* p, int writeError)
    {
        if (writeError == ERROR_DEVICE_NOT_CONNECTED) return HidIoResult.Disconnected;

        // Some Bluetooth stacks / older Wiimotes only accept output reports via SetOutputReport.
        if (HidD_SetOutputReport(_handle, p, _writeBuffer.Length))
        {
            Log.Debug($"WriteFile failed ({writeError}); switching to HidD_SetOutputReport for {Path}");
            _useSetOutputReport = true;
            return HidIoResult.Ok;
        }
        return Classify(writeError);
    }

    private static HidIoResult Classify(int err) => err switch
    {
        ERROR_DEVICE_NOT_CONNECTED or ERROR_GEN_FAILURE or ERROR_OPERATION_ABORTED => HidIoResult.Disconnected,
        _ => HidIoResult.Error,
    };

    /// <summary>Aborts a pending Read/Write so a blocked reader thread wakes up.</summary>
    public void CancelPendingIo()
    {
        if (_disposed) return;
        try { CancelIoEx(_handle, null); } catch { /* ignore */ }
    }

    public void Dispose()
    {
        if (_disposed) return;
        try { CancelIoEx(_handle, null); } catch { /* ignore */ }

        // Wait for an in-flight Write to finish (it was just cancelled) before freeing its memory.
        // The owner must have stopped its reader thread before calling Dispose.
        lock (_writeLock)
        {
            if (_disposed) return;
            _disposed = true;
            _handle.Dispose();
            Thread.Sleep(20); // let a just-cancelled read complete before its OVERLAPPED is freed
            NativeMemory.Free(_readOv);
            NativeMemory.Free(_writeOv);
            _readEvent.Dispose();
            _writeEvent.Dispose();
        }
    }
}

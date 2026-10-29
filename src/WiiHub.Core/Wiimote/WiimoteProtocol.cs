namespace WiiHub.Core.Wiimote;

/// <summary>
/// Pure parsing / building of Wiimote HID reports (see wiibrew.org "Wiimote" pages).
/// No I/O here, so everything is unit-testable.
/// </summary>
public static class WiimoteProtocol
{
    public const ushort VendorId = 0x057E;
    public const ushort ProductIdOriginal = 0x0306;
    public const ushort ProductIdMotionPlusInside = 0x0330;

    // Output report ids
    public const byte OutRumble = 0x10;
    public const byte OutLeds = 0x11;
    public const byte OutReportMode = 0x12;
    public const byte OutStatusRequest = 0x15;
    public const byte OutWriteMemory = 0x16;
    public const byte OutReadMemory = 0x17;

    // Input report ids
    public const byte InStatus = 0x20;
    public const byte InReadData = 0x21;
    public const byte InAck = 0x22;

    // Data reporting modes we use
    public const byte ModeButtonsAccel = 0x31;
    public const byte ModeButtonsAccelExt16 = 0x35;

    // Extension registers
    public const int RegExtInit1 = 0xA400F0;
    public const int RegExtInit2 = 0xA400FB;
    public const int RegExtId = 0xA400FA;
    public const int EepromAccelCalibration = 0x0016;

    private const ushort ButtonMask = 0x1F9F; // ignores accel LSB bits mixed into the button bytes

    public static WiimoteButtons ParseButtons(ReadOnlySpan<byte> report) =>
        (WiimoteButtons)(((report[1] << 8) | report[2]) & ButtonMask);

    /// <summary>True for every input report that carries the core buttons (0x20..0x3F except 0x3D).</summary>
    public static bool HasCoreButtons(byte reportId) => reportId is >= 0x20 and <= 0x3F and not 0x3D;

    /// <summary>Accelerometer sample for reports 0x31, 0x33, 0x35, 0x37, 0x3E/0x3F excluded.</summary>
    public static bool TryParseAccel(ReadOnlySpan<byte> report, out AccelRaw accel)
    {
        accel = default;
        if (report.Length < 6) return false;
        if (report[0] is not (0x31 or 0x33 or 0x35 or 0x37)) return false;
        int x = (report[3] << 2) | ((report[1] >> 5) & 0x03);
        int y = (report[4] << 2) | ((report[2] >> 4) & 0x02);
        int z = (report[5] << 2) | ((report[2] >> 5) & 0x02);
        accel = new AccelRaw(x, y, z);
        return true;
    }

    /// <summary>Returns the extension bytes inside a data report, or an empty span.</summary>
    public static ReadOnlySpan<byte> GetExtensionBytes(ReadOnlySpan<byte> report)
    {
        (int offset, int length) = report[0] switch
        {
            0x32 => (3, 8),
            0x34 => (3, 19),
            0x35 => (6, 16),
            0x36 => (13, 9),
            0x37 => (16, 6),
            0x3D => (1, 21),
            _ => (0, 0),
        };
        if (length == 0 || report.Length < offset + length) return ReadOnlySpan<byte>.Empty;
        return report.Slice(offset, length);
    }

    public sealed record StatusReport(WiimoteButtons Buttons, bool ExtensionConnected, bool BatteryLow, int BatteryRaw, byte Leds)
    {
        /// <summary>Approximate battery percentage; 0xC8 is roughly a fresh pair of batteries.</summary>
        public int BatteryPercent => Math.Clamp(BatteryRaw * 100 / 0xC8, 0, 100);
    }

    public static StatusReport ParseStatus(ReadOnlySpan<byte> report)
    {
        byte flags = report[3];
        return new StatusReport(
            ParseButtons(report),
            ExtensionConnected: (flags & 0x02) != 0,
            BatteryLow: (flags & 0x01) != 0,
            BatteryRaw: report[6],
            Leds: (byte)(flags >> 4));
    }

    public static ExtensionType IdentifyExtension(ReadOnlySpan<byte> id)
    {
        if (id.Length < 6) return ExtensionType.Unknown;
        if (id[2] == 0xA4 && id[3] == 0x20)
        {
            if (id[4] == 0x00 && id[5] == 0x00) return ExtensionType.Nunchuk;
            if (id[4] == 0x01 && id[5] == 0x01) return ExtensionType.ClassicController;
            if (id[4] == 0x01 && id[5] == 0x03)
            {
                return id[0] switch
                {
                    0x00 => ExtensionType.Guitar,
                    0x01 => ExtensionType.Drums,
                    0x03 => ExtensionType.Turntable,
                    _ => ExtensionType.Unknown,
                };
            }
            if (id[4] == 0x04 && id[5] == 0x05) return ExtensionType.MotionPlus;
        }
        return ExtensionType.Unknown;
    }

    /// <summary>
    /// Parses the 6 guitar bytes (unencrypted, after the 0x55/0x00 init). Buttons are active low.
    /// </summary>
    public static GuitarState ParseGuitar(ReadOnlySpan<byte> e)
    {
        byte b4 = (byte)~e[4];
        byte b5 = (byte)~e[5];
        var frets = GuitarFrets.None;
        if ((b5 & 0x10) != 0) frets |= GuitarFrets.Green;
        if ((b5 & 0x40) != 0) frets |= GuitarFrets.Red;
        if ((b5 & 0x08) != 0) frets |= GuitarFrets.Yellow;
        if ((b5 & 0x20) != 0) frets |= GuitarFrets.Blue;
        if ((b5 & 0x80) != 0) frets |= GuitarFrets.Orange;

        return new GuitarState
        {
            Frets = frets,
            StrumUp = (b5 & 0x01) != 0,
            StrumDown = (b4 & 0x40) != 0,
            Plus = (b4 & 0x04) != 0,
            Minus = (b4 & 0x10) != 0,
            StickXRaw = e[0] & 0x3F,
            StickYRaw = e[1] & 0x3F,
            TouchBarRaw = e[2] & 0x1F,
            WhammyRaw = e[3] & 0x1F,
            IsGh3 = (e[0] & 0xC0) != 0,
        };
    }

    public static NunchukState ParseNunchuk(ReadOnlySpan<byte> e)
    {
        byte b5 = e[5];
        return new NunchukState
        {
            StickXRaw = e[0],
            StickYRaw = e[1],
            Accel = new AccelRaw(
                (e[2] << 2) | ((b5 >> 2) & 0x03),
                (e[3] << 2) | ((b5 >> 4) & 0x03),
                (e[4] << 2) | ((b5 >> 6) & 0x03)),
            Z = (b5 & 0x01) == 0,
            C = (b5 & 0x02) == 0,
        };
    }

    /// <summary>
    /// Converts a GHWT touch-bar value into the fret(s) being touched.
    /// 0x0F means "not touched". Two adjacent frets can be held together.
    /// </summary>
    public static GuitarFrets TouchBarToFrets(int raw) => raw switch
    {
        >= 0x01 and <= 0x05 => GuitarFrets.Green,
        >= 0x06 and <= 0x08 => GuitarFrets.Green | GuitarFrets.Red,
        >= 0x09 and <= 0x0B => GuitarFrets.Red,
        >= 0x0C and <= 0x0D => GuitarFrets.Red | GuitarFrets.Yellow,
        >= 0x12 and <= 0x13 => GuitarFrets.Yellow,
        >= 0x14 and <= 0x15 => GuitarFrets.Yellow | GuitarFrets.Blue,
        >= 0x16 and <= 0x18 => GuitarFrets.Blue,
        >= 0x19 and <= 0x1A => GuitarFrets.Blue | GuitarFrets.Orange,
        >= 0x1B and <= 0x1F => GuitarFrets.Orange,
        _ => GuitarFrets.None, // 0x00, 0x0E..0x11 (0x0F = idle)
    };

    /// <summary>Parses the 10-byte accelerometer calibration block from EEPROM 0x0016.</summary>
    public static AccelCalibration? ParseAccelCalibration(ReadOnlySpan<byte> d)
    {
        if (d.Length < 8) return null;
        int x0 = (d[0] << 2) | ((d[3] >> 4) & 0x03);
        int y0 = (d[1] << 2) | ((d[3] >> 2) & 0x03);
        int z0 = (d[2] << 2) | (d[3] & 0x03);
        int xg = (d[4] << 2) | ((d[7] >> 4) & 0x03);
        int yg = (d[5] << 2) | ((d[7] >> 2) & 0x03);
        int zg = (d[6] << 2) | (d[7] & 0x03);
        // Reject obviously broken blocks (e.g. all 0xFF or zero spans).
        if (xg <= x0 || yg <= y0 || zg <= z0 || x0 == 0 || x0 >= 1000) return null;
        return new AccelCalibration(x0, y0, z0, xg, yg, zg);
    }

    // ------------------------------------------------------------------ output builders

    public static byte LedMask(int playerIndex) => playerIndex switch
    {
        1 => 0x1,
        2 => 0x2,
        3 => 0x4,
        4 => 0x8,
        5 => 0x9,
        6 => 0xA,
        7 => 0xC,
        _ => 0xF,
    };

    public static byte[] BuildLeds(byte ledMask, bool rumble) =>
        [OutLeds, (byte)((ledMask << 4) | (rumble ? 1 : 0))];

    public static byte[] BuildRumble(bool rumble) =>
        [OutRumble, (byte)(rumble ? 1 : 0)];

    public static byte[] BuildReportMode(byte mode, bool continuous, bool rumble) =>
        [OutReportMode, (byte)((continuous ? 0x04 : 0x00) | (rumble ? 1 : 0)), mode];

    public static byte[] BuildStatusRequest(bool rumble) =>
        [OutStatusRequest, (byte)(rumble ? 1 : 0)];

    /// <summary>Write up to 16 bytes to the register space (0x04) or EEPROM (0x00).</summary>
    public static byte[] BuildWriteMemory(int address, ReadOnlySpan<byte> data, bool registers, bool rumble)
    {
        if (data.Length is < 1 or > 16) throw new ArgumentOutOfRangeException(nameof(data));
        var r = new byte[22];
        r[0] = OutWriteMemory;
        r[1] = (byte)((registers ? 0x04 : 0x00) | (rumble ? 1 : 0));
        r[2] = (byte)(address >> 16);
        r[3] = (byte)(address >> 8);
        r[4] = (byte)address;
        r[5] = (byte)data.Length;
        data.CopyTo(r.AsSpan(6));
        return r;
    }

    public static byte[] BuildReadMemory(int address, int size, bool registers, bool rumble) =>
    [
        OutReadMemory,
        (byte)((registers ? 0x04 : 0x00) | (rumble ? 1 : 0)),
        (byte)(address >> 16), (byte)(address >> 8), (byte)address,
        (byte)(size >> 8), (byte)size,
    ];
}

namespace WiiHub.Core.Wiimote;

/// <summary>Core Wiimote buttons, packed as (byte1 &lt;&lt; 8) | byte2 of the input report.</summary>
[Flags]
public enum WiimoteButtons : ushort
{
    None = 0,
    Two = 0x0001,
    One = 0x0002,
    B = 0x0004,
    A = 0x0008,
    Minus = 0x0010,
    Home = 0x0080,
    Left = 0x0100,
    Right = 0x0200,
    Down = 0x0400,
    Up = 0x0800,
    Plus = 0x1000,
}

public enum ExtensionType
{
    None,
    Unknown,
    Nunchuk,
    ClassicController,
    Guitar,
    Drums,
    Turntable,
    MotionPlus,
}

public enum WiimoteModel
{
    /// <summary>RVL-CNT-01 (PID 0x0306).</summary>
    Original,
    /// <summary>RVL-CNT-01-TR, Motion Plus inside (PID 0x0330).</summary>
    MotionPlusInside,
}

[Flags]
public enum GuitarFrets : byte
{
    None = 0,
    Green = 0x01,
    Red = 0x02,
    Yellow = 0x04,
    Blue = 0x08,
    Orange = 0x10,
}

/// <summary>Raw 10-bit accelerometer sample.</summary>
public readonly record struct AccelRaw(int X, int Y, int Z);

/// <summary>Accelerometer calibration read from the Wiimote's EEPROM (0x0016).</summary>
public sealed record AccelCalibration(int ZeroX, int ZeroY, int ZeroZ, int GravityX, int GravityY, int GravityZ)
{
    public static AccelCalibration Default { get; } = new(512, 512, 512, 616, 616, 616);

    public double ToG(int raw, int zero, int gravity)
    {
        int span = gravity - zero;
        return span == 0 ? 0 : (raw - zero) / (double)span;
    }

    public (double X, double Y, double Z) ToG(AccelRaw a) =>
        (ToG(a.X, ZeroX, GravityX), ToG(a.Y, ZeroY, GravityY), ToG(a.Z, ZeroZ, GravityZ));
}

public sealed record GuitarState
{
    public GuitarFrets Frets { get; init; }
    public bool StrumUp { get; init; }
    public bool StrumDown { get; init; }
    public bool Plus { get; init; }
    public bool Minus { get; init; }
    /// <summary>Raw whammy value (5 bits, 0..31).</summary>
    public int WhammyRaw { get; init; }
    /// <summary>Raw stick values (6 bits, 0..63, centre ≈ 32).</summary>
    public int StickXRaw { get; init; }
    public int StickYRaw { get; init; }
    /// <summary>Raw touch/slider bar value (5 bits). 0x0F = not touched. GHWT guitars only.</summary>
    public int TouchBarRaw { get; init; }
    /// <summary>True for the Guitar Hero 3 Les Paul (no touch bar).</summary>
    public bool IsGh3 { get; init; }
}

public sealed record NunchukState
{
    public bool C { get; init; }
    public bool Z { get; init; }
    /// <summary>Raw stick (0..255, centre ≈ 128).</summary>
    public int StickXRaw { get; init; }
    public int StickYRaw { get; init; }
    public AccelRaw Accel { get; init; }
}

/// <summary>Immutable snapshot of everything a Wiimote reports.</summary>
public sealed record WiimoteState
{
    public WiimoteButtons Buttons { get; init; }
    public AccelRaw Accel { get; init; } = new(512, 512, 616);
    public bool HasAccel { get; init; }
    public ExtensionType Extension { get; init; }
    public GuitarState? Guitar { get; init; }
    public NunchukState? Nunchuk { get; init; }
    /// <summary>Battery level 0..100 (approximate), or -1 if unknown.</summary>
    public int BatteryPercent { get; init; } = -1;
    public bool BatteryLow { get; init; }

    public bool IsPressed(WiimoteButtons b) => (Buttons & b) != 0;
}

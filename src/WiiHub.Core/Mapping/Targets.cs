namespace WiiHub.Core.Mapping;

public enum OutputKind
{
    Xbox360,
    Keyboard,
}

/// <summary>Everything a binding can drive on the virtual Xbox 360 controller.</summary>
public enum XboxTarget
{
    A,
    B,
    X,
    Y,
    LeftShoulder,
    RightShoulder,
    Back,
    Start,
    Guide,
    LeftThumb,
    RightThumb,
    DPadUp,
    DPadDown,
    DPadLeft,
    DPadRight,
    LeftTrigger,
    RightTrigger,
    LeftStickX,
    LeftStickY,
    RightStickX,
    RightStickY,
}

public static class XboxTargets
{
    public static IReadOnlyList<XboxTarget> All { get; } = Enum.GetValues<XboxTarget>();

    /// <summary>XUSB button bit for button targets, 0 otherwise.</summary>
    public static ushort ButtonMask(XboxTarget t) => t switch
    {
        XboxTarget.DPadUp => 0x0001,
        XboxTarget.DPadDown => 0x0002,
        XboxTarget.DPadLeft => 0x0004,
        XboxTarget.DPadRight => 0x0008,
        XboxTarget.Start => 0x0010,
        XboxTarget.Back => 0x0020,
        XboxTarget.LeftThumb => 0x0040,
        XboxTarget.RightThumb => 0x0080,
        XboxTarget.LeftShoulder => 0x0100,
        XboxTarget.RightShoulder => 0x0200,
        XboxTarget.Guide => 0x0400,
        XboxTarget.A => 0x1000,
        XboxTarget.B => 0x2000,
        XboxTarget.X => 0x4000,
        XboxTarget.Y => 0x8000,
        _ => 0,
    };

    public static bool IsTrigger(XboxTarget t) => t is XboxTarget.LeftTrigger or XboxTarget.RightTrigger;

    public static bool IsAxis(XboxTarget t) =>
        t is XboxTarget.LeftStickX or XboxTarget.LeftStickY or XboxTarget.RightStickX or XboxTarget.RightStickY;

    public static string DisplayName(XboxTarget t) => t switch
    {
        XboxTarget.A => "A",
        XboxTarget.B => "B",
        XboxTarget.X => "X",
        XboxTarget.Y => "Y",
        XboxTarget.LeftShoulder => "LB",
        XboxTarget.RightShoulder => "RB",
        XboxTarget.Back => "Back",
        XboxTarget.Start => "Start",
        XboxTarget.Guide => "Guide",
        XboxTarget.LeftThumb => "Left stick click",
        XboxTarget.RightThumb => "Right stick click",
        XboxTarget.DPadUp => "D-pad Up",
        XboxTarget.DPadDown => "D-pad Down",
        XboxTarget.DPadLeft => "D-pad Left",
        XboxTarget.DPadRight => "D-pad Right",
        XboxTarget.LeftTrigger => "LT",
        XboxTarget.RightTrigger => "RT",
        XboxTarget.LeftStickX => "Left stick X",
        XboxTarget.LeftStickY => "Left stick Y",
        XboxTarget.RightStickX => "Right stick X",
        XboxTarget.RightStickY => "Right stick Y",
        _ => t.ToString(),
    };
}

/// <summary>The full state of one virtual Xbox 360 controller.</summary>
public struct XboxReport : IEquatable<XboxReport>
{
    public ushort Buttons;
    public byte LeftTrigger;
    public byte RightTrigger;
    public short LeftStickX;
    public short LeftStickY;
    public short RightStickX;
    public short RightStickY;

    public readonly bool Equals(XboxReport o) =>
        Buttons == o.Buttons && LeftTrigger == o.LeftTrigger && RightTrigger == o.RightTrigger &&
        LeftStickX == o.LeftStickX && LeftStickY == o.LeftStickY &&
        RightStickX == o.RightStickX && RightStickY == o.RightStickY;

    public override readonly bool Equals(object? obj) => obj is XboxReport r && Equals(r);

    public override readonly int GetHashCode() =>
        HashCode.Combine(Buttons, LeftTrigger, RightTrigger, LeftStickX, LeftStickY, RightStickX, RightStickY);

    /// <summary>Short human-readable list of what is active, for the "outputs" indicator.</summary>
    public readonly string Describe()
    {
        var parts = new List<string>();
        foreach (var t in XboxTargets.All)
        {
            ushort mask = XboxTargets.ButtonMask(t);
            if (mask != 0 && (Buttons & mask) != 0) parts.Add(XboxTargets.DisplayName(t));
        }
        if (LeftTrigger > 20) parts.Add($"LT {LeftTrigger * 100 / 255}%");
        if (RightTrigger > 20) parts.Add($"RT {RightTrigger * 100 / 255}%");
        AddAxis(parts, "LX", LeftStickX);
        AddAxis(parts, "LY", LeftStickY);
        AddAxis(parts, "RX", RightStickX);
        AddAxis(parts, "RY", RightStickY);
        return parts.Count == 0 ? "—" : string.Join("  ", parts);
    }

    private static void AddAxis(List<string> parts, string name, short v)
    {
        if (Math.Abs((int)v) > 6000) parts.Add($"{name} {v * 100 / 32767:+0;-0}%");
    }
}

namespace WiiHub.Core.Input;

/// <summary>Every input the app can read, from the Wiimote itself or an extension.</summary>
public enum InputId
{
    // Wiimote (names as printed on the controller)
    WiimoteA,
    WiimoteB,
    WiimoteOne,
    WiimoteTwo,
    WiimotePlus,
    WiimoteMinus,
    WiimoteHome,
    WiimoteUp,
    WiimoteDown,
    WiimoteLeft,
    WiimoteRight,
    WiimoteAccelX,
    WiimoteAccelY,
    WiimoteAccelZ,

    // Guitar Hero guitar
    GuitarGreen,
    GuitarRed,
    GuitarYellow,
    GuitarBlue,
    GuitarOrange,
    GuitarStrumUp,
    GuitarStrumDown,
    GuitarPlus,
    GuitarMinus,
    GuitarWhammy,
    GuitarTilt,
    GuitarTiltAmount,
    GuitarStickX,
    GuitarStickY,
    GuitarStickUp,
    GuitarStickDown,
    GuitarStickLeft,
    GuitarStickRight,
    GuitarTouchGreen,
    GuitarTouchRed,
    GuitarTouchYellow,
    GuitarTouchBlue,
    GuitarTouchOrange,

    // Nunchuk
    NunchukC,
    NunchukZ,
    NunchukStickX,
    NunchukStickY,
    NunchukStickUp,
    NunchukStickDown,
    NunchukStickLeft,
    NunchukStickRight,
}

public enum InputKind
{
    /// <summary>0 or 1.</summary>
    Button,
    /// <summary>0..1 (whammy, tilt amount).</summary>
    Unipolar,
    /// <summary>-1..1 (sticks, accelerometer).</summary>
    Bipolar,
}

public enum InputGroup { Wiimote, Guitar, Nunchuk }

public sealed record InputInfo(InputId Id, string Name, InputKind Kind, InputGroup Group);

public static class InputCatalog
{
    public const int Count = (int)InputId.NunchukStickRight + 1;

    private static readonly InputInfo[] All =
    [
        new(InputId.WiimoteA, "Wiimote A", InputKind.Button, InputGroup.Wiimote),
        new(InputId.WiimoteB, "Wiimote B (trigger)", InputKind.Button, InputGroup.Wiimote),
        new(InputId.WiimoteOne, "Wiimote 1", InputKind.Button, InputGroup.Wiimote),
        new(InputId.WiimoteTwo, "Wiimote 2", InputKind.Button, InputGroup.Wiimote),
        new(InputId.WiimotePlus, "Wiimote +", InputKind.Button, InputGroup.Wiimote),
        new(InputId.WiimoteMinus, "Wiimote −", InputKind.Button, InputGroup.Wiimote),
        new(InputId.WiimoteHome, "Wiimote Home", InputKind.Button, InputGroup.Wiimote),
        new(InputId.WiimoteUp, "Wiimote D-pad Up", InputKind.Button, InputGroup.Wiimote),
        new(InputId.WiimoteDown, "Wiimote D-pad Down", InputKind.Button, InputGroup.Wiimote),
        new(InputId.WiimoteLeft, "Wiimote D-pad Left", InputKind.Button, InputGroup.Wiimote),
        new(InputId.WiimoteRight, "Wiimote D-pad Right", InputKind.Button, InputGroup.Wiimote),
        new(InputId.WiimoteAccelX, "Wiimote tilt X", InputKind.Bipolar, InputGroup.Wiimote),
        new(InputId.WiimoteAccelY, "Wiimote tilt Y", InputKind.Bipolar, InputGroup.Wiimote),
        new(InputId.WiimoteAccelZ, "Wiimote tilt Z", InputKind.Bipolar, InputGroup.Wiimote),

        new(InputId.GuitarGreen, "Green fret", InputKind.Button, InputGroup.Guitar),
        new(InputId.GuitarRed, "Red fret", InputKind.Button, InputGroup.Guitar),
        new(InputId.GuitarYellow, "Yellow fret", InputKind.Button, InputGroup.Guitar),
        new(InputId.GuitarBlue, "Blue fret", InputKind.Button, InputGroup.Guitar),
        new(InputId.GuitarOrange, "Orange fret", InputKind.Button, InputGroup.Guitar),
        new(InputId.GuitarStrumUp, "Strum up", InputKind.Button, InputGroup.Guitar),
        new(InputId.GuitarStrumDown, "Strum down", InputKind.Button, InputGroup.Guitar),
        new(InputId.GuitarPlus, "Guitar + (Start)", InputKind.Button, InputGroup.Guitar),
        new(InputId.GuitarMinus, "Guitar − (Star Power)", InputKind.Button, InputGroup.Guitar),
        new(InputId.GuitarWhammy, "Whammy bar", InputKind.Unipolar, InputGroup.Guitar),
        new(InputId.GuitarTilt, "Tilt (Star Power)", InputKind.Button, InputGroup.Guitar),
        new(InputId.GuitarTiltAmount, "Tilt amount", InputKind.Unipolar, InputGroup.Guitar),
        new(InputId.GuitarStickX, "Guitar stick X", InputKind.Bipolar, InputGroup.Guitar),
        new(InputId.GuitarStickY, "Guitar stick Y", InputKind.Bipolar, InputGroup.Guitar),
        new(InputId.GuitarStickUp, "Guitar stick Up", InputKind.Button, InputGroup.Guitar),
        new(InputId.GuitarStickDown, "Guitar stick Down", InputKind.Button, InputGroup.Guitar),
        new(InputId.GuitarStickLeft, "Guitar stick Left", InputKind.Button, InputGroup.Guitar),
        new(InputId.GuitarStickRight, "Guitar stick Right", InputKind.Button, InputGroup.Guitar),
        new(InputId.GuitarTouchGreen, "Touch bar Green", InputKind.Button, InputGroup.Guitar),
        new(InputId.GuitarTouchRed, "Touch bar Red", InputKind.Button, InputGroup.Guitar),
        new(InputId.GuitarTouchYellow, "Touch bar Yellow", InputKind.Button, InputGroup.Guitar),
        new(InputId.GuitarTouchBlue, "Touch bar Blue", InputKind.Button, InputGroup.Guitar),
        new(InputId.GuitarTouchOrange, "Touch bar Orange", InputKind.Button, InputGroup.Guitar),

        new(InputId.NunchukC, "Nunchuk C", InputKind.Button, InputGroup.Nunchuk),
        new(InputId.NunchukZ, "Nunchuk Z", InputKind.Button, InputGroup.Nunchuk),
        new(InputId.NunchukStickX, "Nunchuk stick X", InputKind.Bipolar, InputGroup.Nunchuk),
        new(InputId.NunchukStickY, "Nunchuk stick Y", InputKind.Bipolar, InputGroup.Nunchuk),
        new(InputId.NunchukStickUp, "Nunchuk stick Up", InputKind.Button, InputGroup.Nunchuk),
        new(InputId.NunchukStickDown, "Nunchuk stick Down", InputKind.Button, InputGroup.Nunchuk),
        new(InputId.NunchukStickLeft, "Nunchuk stick Left", InputKind.Button, InputGroup.Nunchuk),
        new(InputId.NunchukStickRight, "Nunchuk stick Right", InputKind.Button, InputGroup.Nunchuk),
    ];

    static InputCatalog()
    {
        for (int i = 0; i < All.Length; i++)
        {
            if ((int)All[i].Id != i) throw new InvalidOperationException($"InputCatalog out of order at {All[i].Id}");
        }
        if (All.Length != Count) throw new InvalidOperationException("InputCatalog incomplete");
    }

    public static IReadOnlyList<InputInfo> Inputs => All;

    public static InputInfo Get(InputId id) => All[(int)id];

    /// <summary>Inputs that exist for a controller with the given extension.</summary>
    public static IEnumerable<InputInfo> For(DeviceKind kind) => All.Where(i =>
        i.Group == InputGroup.Wiimote ||
        (kind == DeviceKind.Guitar && i.Group == InputGroup.Guitar) ||
        (kind == DeviceKind.Nunchuk && i.Group == InputGroup.Nunchuk));
}

/// <summary>Which mapping set applies: chosen by the plugged-in extension.</summary>
public enum DeviceKind
{
    Wiimote,
    Guitar,
    Nunchuk,
}

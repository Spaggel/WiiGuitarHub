using WiiHub.Core.Config;
using WiiHub.Core.Input;
using static WiiHub.Core.Input.InputId;

namespace WiiHub.Core.Mapping;

/// <summary>Out-of-the-box bindings. Everything is editable in the UI.</summary>
public static class DefaultMappings
{
    public static List<Binding> Create(DeviceKind device, OutputKind output) => (device, output) switch
    {
        (DeviceKind.Guitar, OutputKind.Xbox360) => GuitarXbox(),
        (DeviceKind.Guitar, OutputKind.Keyboard) => GuitarKeyboard(),
        (DeviceKind.Nunchuk, OutputKind.Xbox360) => NunchukXbox(),
        (DeviceKind.Nunchuk, OutputKind.Keyboard) => NunchukKeyboard(),
        (_, OutputKind.Xbox360) => WiimoteXbox(),
        _ => WiimoteKeyboard(),
    };

    private static Binding X(InputId source, XboxTarget target, bool invert = false) => new(source, (int)target, invert);
    private static Binding K(InputId source, int vk) => new(source, vk);

    /// <summary>Follows the layout of Xbox 360 Guitar Hero / Rock Band guitars.</summary>
    private static List<Binding> GuitarXbox() =>
    [
        X(GuitarGreen, XboxTarget.A),
        X(GuitarRed, XboxTarget.B),
        X(GuitarYellow, XboxTarget.Y),
        X(GuitarBlue, XboxTarget.X),
        X(GuitarOrange, XboxTarget.LeftShoulder),
        X(GuitarStrumUp, XboxTarget.DPadUp),
        X(GuitarStrumDown, XboxTarget.DPadDown),
        X(GuitarPlus, XboxTarget.Start),
        X(GuitarMinus, XboxTarget.Back),
        X(GuitarWhammy, XboxTarget.RightStickX),
        X(GuitarTilt, XboxTarget.RightStickY),
        X(GuitarStickX, XboxTarget.LeftStickX),
        X(GuitarStickY, XboxTarget.LeftStickY),
        X(GuitarTouchGreen, XboxTarget.A),
        X(GuitarTouchRed, XboxTarget.B),
        X(GuitarTouchYellow, XboxTarget.Y),
        X(GuitarTouchBlue, XboxTarget.X),
        X(GuitarTouchOrange, XboxTarget.LeftShoulder),
        X(WiimoteHome, XboxTarget.Guide),
    ];

    private static List<Binding> GuitarKeyboard() =>
    [
        K(GuitarGreen, '1'),
        K(GuitarRed, '2'),
        K(GuitarYellow, '3'),
        K(GuitarBlue, '4'),
        K(GuitarOrange, '5'),
        K(GuitarStrumUp, Keys.Up),
        K(GuitarStrumDown, Keys.Down),
        K(GuitarPlus, Keys.Enter),
        K(GuitarMinus, Keys.Space),
        K(GuitarTilt, Keys.Space),
        K(GuitarWhammy, 'W'),
        K(GuitarStickUp, 'I'),
        K(GuitarStickDown, 'K'),
        K(GuitarStickLeft, 'J'),
        K(GuitarStickRight, 'L'),
        K(WiimoteHome, Keys.Escape),
    ];

    /// <summary>Wiimote held sideways (D-pad on the left), NES-style.</summary>
    private static List<Binding> WiimoteXbox() =>
    [
        X(WiimoteUp, XboxTarget.DPadLeft),
        X(WiimoteDown, XboxTarget.DPadRight),
        X(WiimoteLeft, XboxTarget.DPadDown),
        X(WiimoteRight, XboxTarget.DPadUp),
        X(WiimoteTwo, XboxTarget.A),
        X(WiimoteOne, XboxTarget.B),
        X(WiimoteA, XboxTarget.Y),
        X(WiimoteB, XboxTarget.X),
        X(WiimotePlus, XboxTarget.Start),
        X(WiimoteMinus, XboxTarget.Back),
        X(WiimoteHome, XboxTarget.Guide),
    ];

    private static List<Binding> WiimoteKeyboard() =>
    [
        K(WiimoteUp, Keys.Left),
        K(WiimoteDown, Keys.Right),
        K(WiimoteLeft, Keys.Down),
        K(WiimoteRight, Keys.Up),
        K(WiimoteTwo, 'X'),
        K(WiimoteOne, 'Z'),
        K(WiimoteA, 'A'),
        K(WiimoteB, 'S'),
        K(WiimotePlus, Keys.Enter),
        K(WiimoteMinus, Keys.Backspace),
        K(WiimoteHome, Keys.Escape),
    ];

    /// <summary>Wiimote upright + Nunchuk.</summary>
    private static List<Binding> NunchukXbox() =>
    [
        X(NunchukStickX, XboxTarget.LeftStickX),
        X(NunchukStickY, XboxTarget.LeftStickY),
        X(NunchukZ, XboxTarget.LeftTrigger),
        X(NunchukC, XboxTarget.LeftShoulder),
        X(WiimoteA, XboxTarget.A),
        X(WiimoteB, XboxTarget.RightTrigger),
        X(WiimoteOne, XboxTarget.X),
        X(WiimoteTwo, XboxTarget.Y),
        X(WiimoteUp, XboxTarget.DPadUp),
        X(WiimoteDown, XboxTarget.DPadDown),
        X(WiimoteLeft, XboxTarget.DPadLeft),
        X(WiimoteRight, XboxTarget.DPadRight),
        X(WiimotePlus, XboxTarget.Start),
        X(WiimoteMinus, XboxTarget.Back),
        X(WiimoteHome, XboxTarget.Guide),
    ];

    private static List<Binding> NunchukKeyboard() =>
    [
        K(NunchukStickUp, 'W'),
        K(NunchukStickDown, 'S'),
        K(NunchukStickLeft, 'A'),
        K(NunchukStickRight, 'D'),
        K(NunchukZ, Keys.Space),
        K(NunchukC, Keys.LShift),
        K(WiimoteA, 'E'),
        K(WiimoteB, 'F'),
        K(WiimoteOne, '1'),
        K(WiimoteTwo, '2'),
        K(WiimoteUp, Keys.Up),
        K(WiimoteDown, Keys.Down),
        K(WiimoteLeft, Keys.Left),
        K(WiimoteRight, Keys.Right),
        K(WiimotePlus, Keys.Enter),
        K(WiimoteMinus, Keys.Backspace),
        K(WiimoteHome, Keys.Escape),
    ];
}

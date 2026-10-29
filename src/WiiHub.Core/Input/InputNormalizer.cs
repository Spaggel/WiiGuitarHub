using WiiHub.Core.Config;
using WiiHub.Core.Wiimote;

namespace WiiHub.Core.Input;

/// <summary>
/// Converts raw <see cref="WiimoteState"/> into normalised <see cref="InputSnapshot"/> values.
/// Keeps a little per-controller state: whammy range auto-calibration, tilt hysteresis and
/// whether the touch bar has been seen idle (so guitars without one never get stuck frets).
/// </summary>
public sealed class InputNormalizer
{
    private const float StickDeadzone = 0.15f;
    private const float DirectionThreshold = 0.5f;
    private const double TiltHysteresis = 0.1;

    private int _whammyMin = -1;
    private int _whammyMax = -1;
    private bool _touchBarSeenIdle;
    private bool _tiltOn;

    /// <summary>Forget learned ranges (call when the extension changes).</summary>
    public void Reset()
    {
        _whammyMin = _whammyMax = -1;
        _touchBarSeenIdle = false;
        _tiltOn = false;
    }

    public InputSnapshot Normalize(WiimoteState s, AccelCalibration cal, TiltSettings tilt, bool touchBarEnabled)
    {
        var v = new float[InputCatalog.Count];
        var b = s.Buttons;

        Set(v, InputId.WiimoteA, b.HasFlag(WiimoteButtons.A));
        Set(v, InputId.WiimoteB, b.HasFlag(WiimoteButtons.B));
        Set(v, InputId.WiimoteOne, b.HasFlag(WiimoteButtons.One));
        Set(v, InputId.WiimoteTwo, b.HasFlag(WiimoteButtons.Two));
        Set(v, InputId.WiimotePlus, b.HasFlag(WiimoteButtons.Plus));
        Set(v, InputId.WiimoteMinus, b.HasFlag(WiimoteButtons.Minus));
        Set(v, InputId.WiimoteHome, b.HasFlag(WiimoteButtons.Home));
        Set(v, InputId.WiimoteUp, b.HasFlag(WiimoteButtons.Up));
        Set(v, InputId.WiimoteDown, b.HasFlag(WiimoteButtons.Down));
        Set(v, InputId.WiimoteLeft, b.HasFlag(WiimoteButtons.Left));
        Set(v, InputId.WiimoteRight, b.HasFlag(WiimoteButtons.Right));

        var (gx, gy, gz) = s.HasAccel ? cal.ToG(s.Accel) : (0d, 0d, 1d);
        v[(int)InputId.WiimoteAccelX] = (float)Math.Clamp(gx, -1, 1);
        v[(int)InputId.WiimoteAccelY] = (float)Math.Clamp(gy, -1, 1);
        v[(int)InputId.WiimoteAccelZ] = (float)Math.Clamp(gz, -1, 1);

        double tiltAxisG = tilt.Axis switch { AccelAxis.X => gx, AccelAxis.Y => gy, _ => gz };

        var kind = DeviceKind.Wiimote;
        if (s.Extension == ExtensionType.Guitar)
        {
            kind = DeviceKind.Guitar;
            if (s.Guitar is { } g) FillGuitar(v, g, touchBarEnabled);
            FillTilt(v, s.HasAccel, tiltAxisG, tilt);
        }
        else if (s.Extension == ExtensionType.Nunchuk)
        {
            kind = DeviceKind.Nunchuk;
            if (s.Nunchuk is { } n) FillNunchuk(v, n);
        }

        return new InputSnapshot(kind, s.Extension, v, s, tiltAxisG);
    }

    private void FillGuitar(float[] v, GuitarState g, bool touchBarEnabled)
    {
        Set(v, InputId.GuitarGreen, g.Frets.HasFlag(GuitarFrets.Green));
        Set(v, InputId.GuitarRed, g.Frets.HasFlag(GuitarFrets.Red));
        Set(v, InputId.GuitarYellow, g.Frets.HasFlag(GuitarFrets.Yellow));
        Set(v, InputId.GuitarBlue, g.Frets.HasFlag(GuitarFrets.Blue));
        Set(v, InputId.GuitarOrange, g.Frets.HasFlag(GuitarFrets.Orange));
        Set(v, InputId.GuitarStrumUp, g.StrumUp);
        Set(v, InputId.GuitarStrumDown, g.StrumDown);
        Set(v, InputId.GuitarPlus, g.Plus);
        Set(v, InputId.GuitarMinus, g.Minus);

        // Whammy: rests around 15-16, fully pressed around 26-27. Learn the real range.
        int w = g.WhammyRaw;
        if (_whammyMin < 0) { _whammyMin = w; _whammyMax = w + 10; }
        if (w < _whammyMin) _whammyMin = w;
        if (w > _whammyMax) _whammyMax = w;
        int span = Math.Max(6, _whammyMax - _whammyMin);
        float whammy = Math.Clamp((w - _whammyMin) / (float)span, 0f, 1f);
        v[(int)InputId.GuitarWhammy] = whammy < 0.08f ? 0f : whammy;

        float sx = Deadzone(Math.Clamp((g.StickXRaw - 32) / 31f, -1f, 1f));
        float sy = Deadzone(Math.Clamp((g.StickYRaw - 32) / 31f, -1f, 1f));
        v[(int)InputId.GuitarStickX] = sx;
        v[(int)InputId.GuitarStickY] = sy;
        Set(v, InputId.GuitarStickUp, sy >= DirectionThreshold);
        Set(v, InputId.GuitarStickDown, sy <= -DirectionThreshold);
        Set(v, InputId.GuitarStickLeft, sx <= -DirectionThreshold);
        Set(v, InputId.GuitarStickRight, sx >= DirectionThreshold);

        if (g.TouchBarRaw == 0x0F) _touchBarSeenIdle = true;
        if (touchBarEnabled && !g.IsGh3 && _touchBarSeenIdle)
        {
            var t = WiimoteProtocol.TouchBarToFrets(g.TouchBarRaw);
            Set(v, InputId.GuitarTouchGreen, t.HasFlag(GuitarFrets.Green));
            Set(v, InputId.GuitarTouchRed, t.HasFlag(GuitarFrets.Red));
            Set(v, InputId.GuitarTouchYellow, t.HasFlag(GuitarFrets.Yellow));
            Set(v, InputId.GuitarTouchBlue, t.HasFlag(GuitarFrets.Blue));
            Set(v, InputId.GuitarTouchOrange, t.HasFlag(GuitarFrets.Orange));
        }
    }

    private void FillTilt(float[] v, bool hasAccel, double axisG, TiltSettings tilt)
    {
        if (!hasAccel) return;
        double span = tilt.TiltedG - tilt.RestG;
        if (Math.Abs(span) < 0.05) span = span < 0 ? -0.05 : 0.05;

        double amount = tilt.Calibrated
            ? (axisG - tilt.RestG) / span
            : Math.Abs(axisG - tilt.RestG) / Math.Abs(span);
        amount = Math.Clamp(amount, 0, 1);
        v[(int)InputId.GuitarTiltAmount] = (float)amount;

        if (_tiltOn) _tiltOn = amount >= tilt.Threshold - TiltHysteresis;
        else _tiltOn = amount >= tilt.Threshold + TiltHysteresis / 2;
        Set(v, InputId.GuitarTilt, _tiltOn);
    }

    private static void FillNunchuk(float[] v, NunchukState n)
    {
        Set(v, InputId.NunchukC, n.C);
        Set(v, InputId.NunchukZ, n.Z);
        float sx = Deadzone(Math.Clamp((n.StickXRaw - 128) / 100f, -1f, 1f));
        float sy = Deadzone(Math.Clamp((n.StickYRaw - 128) / 100f, -1f, 1f));
        v[(int)InputId.NunchukStickX] = sx;
        v[(int)InputId.NunchukStickY] = sy;
        Set(v, InputId.NunchukStickUp, sy >= DirectionThreshold);
        Set(v, InputId.NunchukStickDown, sy <= -DirectionThreshold);
        Set(v, InputId.NunchukStickLeft, sx <= -DirectionThreshold);
        Set(v, InputId.NunchukStickRight, sx >= DirectionThreshold);
    }

    private static float Deadzone(float x)
    {
        if (Math.Abs(x) < StickDeadzone) return 0f;
        // Rescale so the output still reaches ±1.
        return Math.Sign(x) * (Math.Abs(x) - StickDeadzone) / (1f - StickDeadzone);
    }

    private static void Set(float[] v, InputId id, bool pressed) => v[(int)id] = pressed ? 1f : 0f;
}

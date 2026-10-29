using WiiHub.Core.Wiimote;

namespace WiiHub.Core.Input;

/// <summary>Normalised values of every <see cref="InputId"/> at one moment in time.</summary>
public sealed class InputSnapshot
{
    private readonly float[] _values;

    public InputSnapshot(DeviceKind kind, ExtensionType extension, float[] values, WiimoteState raw, double tiltAxisG)
    {
        if (values.Length != InputCatalog.Count) throw new ArgumentException("Wrong value count", nameof(values));
        Kind = kind;
        Extension = extension;
        _values = values;
        Raw = raw;
        TiltAxisG = tiltAxisG;
    }

    public static InputSnapshot Empty { get; } =
        new(DeviceKind.Wiimote, ExtensionType.None, new float[InputCatalog.Count], new WiimoteState(), 0);

    public DeviceKind Kind { get; }
    public ExtensionType Extension { get; }
    public WiimoteState Raw { get; }

    /// <summary>Current reading (in g) of the accelerometer axis used for guitar tilt.</summary>
    public double TiltAxisG { get; }

    public float this[InputId id] => _values[(int)id];

    public bool IsPressed(InputId id) => _values[(int)id] >= 0.5f;

    public ReadOnlySpan<float> Values => _values;
}

using WiiHub.Core.Config;
using WiiHub.Core.Input;

namespace WiiHub.Core.Mapping;

/// <summary>
/// Turns an <see cref="InputSnapshot"/> into output state. Several bindings may target the same
/// output: buttons/keys are OR-ed, triggers take the maximum, sticks take the largest deflection.
/// </summary>
public static class MappingEngine
{
    /// <summary>
    /// Value of a binding's source in -1..1 after applying <see cref="Binding.Invert"/>.
    /// Buttons: 0/1 (inverted: 0/−1, so a button can push a stick negative).
    /// Unipolar (whammy…): 0..1 (inverted: 1..0). Bipolar (sticks…): −1..1 (inverted: negated).
    /// </summary>
    public static float SourceValue(InputSnapshot s, Binding b)
    {
        float v = s[b.Source];
        if (!b.Invert) return v;
        return InputCatalog.Get(b.Source).Kind == InputKind.Unipolar ? 1f - v : -v;
    }

    public static bool IsActive(InputSnapshot s, Binding b) => SourceValue(s, b) >= 0.5f;

    public static XboxReport EvaluateXbox(InputSnapshot s, IReadOnlyList<Binding> bindings)
    {
        var r = new XboxReport();
        Span<int> axes = stackalloc int[4];
        Span<bool> axisSet = stackalloc bool[4];

        foreach (var b in bindings)
        {
            var target = (XboxTarget)b.Target;
            float v = SourceValue(s, b);

            ushort mask = XboxTargets.ButtonMask(target);
            if (mask != 0)
            {
                if (v >= 0.5f) r.Buttons |= mask;
                continue;
            }

            if (XboxTargets.IsTrigger(target))
            {
                byte t = (byte)Math.Round(Math.Clamp(v, 0f, 1f) * 255f);
                if (target == XboxTarget.LeftTrigger) r.LeftTrigger = Math.Max(r.LeftTrigger, t);
                else r.RightTrigger = Math.Max(r.RightTrigger, t);
                continue;
            }

            if (XboxTargets.IsAxis(target))
            {
                int value = InputCatalog.Get(b.Source).Kind == InputKind.Unipolar
                    // Whammy-style: rest = one end of the axis, fully pressed = the other end.
                    ? (int)Math.Round(v * 65535f - 32768f)
                    : (int)Math.Round(Math.Clamp(v, -1f, 1f) * 32767f);
                int i = target - XboxTarget.LeftStickX;
                if (!axisSet[i] || Math.Abs(value) > Math.Abs(axes[i]))
                {
                    axes[i] = value;
                    axisSet[i] = true;
                }
            }
        }

        r.LeftStickX = ClampShort(axes[0]);
        r.LeftStickY = ClampShort(axes[1]);
        r.RightStickX = ClampShort(axes[2]);
        r.RightStickY = ClampShort(axes[3]);
        return r;
    }

    public static void EvaluateKeys(InputSnapshot s, IReadOnlyList<Binding> bindings, HashSet<int> pressed)
    {
        pressed.Clear();
        foreach (var b in bindings)
        {
            if (b.Target > 0 && SourceValue(s, b) >= 0.5f) pressed.Add(b.Target);
        }
    }

    public static string DescribeKeys(IEnumerable<int> keys)
    {
        var names = keys.Select(Keys.Name).ToList();
        return names.Count == 0 ? "—" : string.Join("  ", names);
    }

    public static string DescribeTarget(OutputKind output, int target) => output == OutputKind.Xbox360
        ? XboxTargets.DisplayName((XboxTarget)target)
        : Keys.Name(target);

    private static short ClampShort(int v) => (short)Math.Clamp(v, short.MinValue, short.MaxValue);
}

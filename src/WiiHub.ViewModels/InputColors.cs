using WiiHub.Core.Input;
using static WiiHub.Core.Input.InputId;

namespace WiiHub.ViewModels;

/// <summary>Colours used to identify inputs in the UI (same palette as the guitar view).</summary>
public static class InputColors
{
    public sealed record Palette(string Accent, string Soft, bool IsFret);

    public static readonly Palette Green = new("#2DBE60", "#DCF5E5", true);
    public static readonly Palette Red = new("#E5484D", "#FBE1E2", true);
    public static readonly Palette Yellow = new("#E0B000", "#FDF3C7", true);
    public static readonly Palette Blue = new("#2F80ED", "#DDEAFC", true);
    public static readonly Palette Orange = new("#F2853A", "#FDE7D8", true);
    public static readonly Palette Default = new("#3B6FF5", "#E8EEFE", false);

    public static Palette ForInput(InputId id) => id switch
    {
        GuitarGreen or GuitarTouchGreen => Green,
        GuitarRed or GuitarTouchRed => Red,
        GuitarYellow or GuitarTouchYellow => Yellow,
        GuitarBlue or GuitarTouchBlue => Blue,
        GuitarOrange or GuitarTouchOrange => Orange,
        _ => Default,
    };
}

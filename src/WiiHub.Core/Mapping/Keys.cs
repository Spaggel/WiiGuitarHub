namespace WiiHub.Core.Mapping;

/// <summary>Windows virtual-key codes the keyboard output can press, with display names.</summary>
public static class Keys
{
    public sealed record KeyInfo(int VirtualKey, string Name);

    public const int Backspace = 0x08, Tab = 0x09, Enter = 0x0D, Shift = 0x10, Ctrl = 0x11, Alt = 0x12,
        Escape = 0x1B, Space = 0x20, PageUp = 0x21, PageDown = 0x22, End = 0x23, Home = 0x24,
        Left = 0x25, Up = 0x26, Right = 0x27, Down = 0x28, Insert = 0x2D, Delete = 0x2E,
        LShift = 0xA0, RShift = 0xA1, LCtrl = 0xA2, RCtrl = 0xA3, LAlt = 0xA4, RAlt = 0xA5;

    private static readonly List<KeyInfo> List = Build();
    private static readonly Dictionary<int, string> Names = List.ToDictionary(k => k.VirtualKey, k => k.Name);

    public static IReadOnlyList<KeyInfo> All => List;

    public static string Name(int vk) => Names.TryGetValue(vk, out var n) ? n : $"Key 0x{vk:X2}";

    /// <summary>Keys whose scan code needs the "extended" flag (arrows, nav block, right Ctrl/Alt).</summary>
    public static bool IsExtended(int vk) => vk is
        >= PageUp and <= Down or Insert or Delete or RCtrl or RAlt or 0x5B or 0x5C or 0x6F or 0x90;

    private static List<KeyInfo> Build()
    {
        var l = new List<KeyInfo>();
        for (char c = 'A'; c <= 'Z'; c++) l.Add(new(c, c.ToString()));
        for (char c = '0'; c <= '9'; c++) l.Add(new(c, c.ToString()));
        for (int i = 1; i <= 12; i++) l.Add(new(0x70 + i - 1, $"F{i}"));
        l.AddRange(
        [
            new(Up, "Up arrow"), new(Down, "Down arrow"), new(Left, "Left arrow"), new(Right, "Right arrow"),
            new(Space, "Space"), new(Enter, "Enter"), new(Escape, "Esc"), new(Backspace, "Backspace"),
            new(Tab, "Tab"), new(LShift, "Left Shift"), new(RShift, "Right Shift"), new(LCtrl, "Left Ctrl"),
            new(RCtrl, "Right Ctrl"), new(LAlt, "Left Alt"), new(RAlt, "Right Alt"), new(Insert, "Insert"),
            new(Delete, "Delete"), new(Home, "Home"), new(End, "End"), new(PageUp, "Page Up"),
            new(PageDown, "Page Down"),
            new(Shift, "Shift"), new(Ctrl, "Ctrl"), new(Alt, "Alt"),
        ]);
        for (int i = 0; i <= 9; i++) l.Add(new(0x60 + i, $"Numpad {i}"));
        l.AddRange(
        [
            new(0x6A, "Numpad *"), new(0x6B, "Numpad +"), new(0x6D, "Numpad −"), new(0x6E, "Numpad ."),
            new(0x6F, "Numpad /"), new(0xBA, ";"), new(0xBB, "="), new(0xBC, ","), new(0xBD, "-"),
            new(0xBE, "."), new(0xBF, "/"), new(0xC0, "`"), new(0xDB, "["), new(0xDC, "\\"),
            new(0xDD, "]"), new(0xDE, "'"),
        ]);
        return l;
    }
}

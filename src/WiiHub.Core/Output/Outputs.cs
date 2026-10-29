using WiiHub.Core.Mapping;
using WiiHub.Core.Native;

namespace WiiHub.Core.Output;

/// <summary>A virtual Xbox 360 controller (implemented with ViGEmBus in the app).</summary>
public interface IXboxPad : IDisposable
{
    void Submit(in XboxReport report);

    /// <summary>Rumble from the game: (large motor, small motor), 0..255 each.</summary>
    event Action<byte, byte>? RumbleReceived;
}

public interface IXboxPadFactory
{
    /// <summary>False when the driver is missing; <paramref name="message"/> explains why.</summary>
    bool IsAvailable(out string message);

    IXboxPad Create();
}

/// <summary>
/// Shared keyboard injector. Each controller owns a set of held keys; a key stays down while
/// any controller holds it, so two guitars mapped to the same key don't release each other.
/// </summary>
public sealed class KeyboardOutput
{
    public static KeyboardOutput Instance { get; } = new();

    private readonly object _sync = new();
    private readonly Dictionary<object, HashSet<int>> _owners = new();
    private readonly Dictionary<int, int> _refCounts = new();

    private KeyboardOutput() { }

    /// <summary>Sets the full set of keys an owner holds right now; sends only the changes.</summary>
    public void SetKeys(object owner, IReadOnlySet<int> keys)
    {
        lock (_sync)
        {
            if (!_owners.TryGetValue(owner, out var held))
            {
                held = [];
                _owners[owner] = held;
            }

            List<(int vk, bool down)>? events = null;
            foreach (int vk in held)
            {
                if (keys.Contains(vk)) continue;
                if (Release(vk)) (events ??= []).Add((vk, false));
            }
            foreach (int vk in keys)
            {
                if (held.Contains(vk)) continue;
                if (Press(vk)) (events ??= []).Add((vk, true));
            }

            held.Clear();
            held.UnionWith(keys);
            if (events is not null) Send(events);
        }
    }

    public void ReleaseAll(object owner) => SetKeys(owner, new HashSet<int>());

    private bool Press(int vk)
    {
        _refCounts.TryGetValue(vk, out int c);
        _refCounts[vk] = c + 1;
        return c == 0;
    }

    private bool Release(int vk)
    {
        if (!_refCounts.TryGetValue(vk, out int c) || c <= 0) return false;
        if (c == 1)
        {
            _refCounts.Remove(vk);
            return true;
        }
        _refCounts[vk] = c - 1;
        return false;
    }

    private static void Send(List<(int vk, bool down)> events)
    {
        var inputs = new Win32.INPUT[events.Count];
        for (int i = 0; i < events.Count; i++)
        {
            var (vk, down) = events[i];
            ushort scan = (ushort)Win32.MapVirtualKey((uint)vk, Win32.MAPVK_VK_TO_VSC);
            uint flags = Win32.KEYEVENTF_SCANCODE;
            if (!down) flags |= Win32.KEYEVENTF_KEYUP;
            if (Keys.IsExtended(vk)) flags |= Win32.KEYEVENTF_EXTENDEDKEY;
            if (scan == 0)
            {
                // No scan code (rare): fall back to a virtual-key event.
                flags &= ~Win32.KEYEVENTF_SCANCODE;
            }
            inputs[i] = new Win32.INPUT
            {
                type = Win32.INPUT_KEYBOARD,
                U = new Win32.InputUnion
                {
                    ki = new Win32.KEYBDINPUT { wVk = (ushort)vk, wScan = scan, dwFlags = flags },
                },
            };
        }
        uint sent = Win32.SendInput((uint)inputs.Length, inputs, System.Runtime.InteropServices.Marshal.SizeOf<Win32.INPUT>());
        if (sent != inputs.Length) Log.Debug($"SendInput sent {sent}/{inputs.Length} events.");
    }
}

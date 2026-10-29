using System.Text;

namespace WiiHub.Core.Bluetooth;

/// <summary>Helpers for 48-bit Bluetooth addresses (as stored in BLUETOOTH_ADDRESS.ullLong).</summary>
public static class BtAddress
{
    /// <summary>Formats as "00:1F:32:AB:CD:EF" (most significant byte first, as Windows shows it).</summary>
    public static string Format(ulong address)
    {
        var sb = new StringBuilder(17);
        for (int i = 5; i >= 0; i--)
        {
            sb.Append(((byte)(address >> (8 * i))).ToString("X2"));
            if (i > 0) sb.Append(':');
        }
        return sb.ToString();
    }

    /// <summary>
    /// Normalises any hex representation ("001f32abcdef", "00-1F-32-AB-CD-EF", ...) to the
    /// "00:1F:32:AB:CD:EF" form. Returns null if it does not contain exactly 12 hex digits.
    /// </summary>
    public static string? Normalize(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var hex = new StringBuilder(12);
        foreach (char c in text)
        {
            if (Uri.IsHexDigit(c)) hex.Append(char.ToUpperInvariant(c));
        }
        if (hex.Length != 12) return null;
        var s = hex.ToString();
        return string.Join(':', Enumerable.Range(0, 6).Select(i => s.Substring(i * 2, 2)));
    }

    /// <summary>
    /// The 6 raw address bytes in BLUETOOTH_ADDRESS.rgBytes order (least significant first).
    /// This is exactly the byte order the Wiimote expects as its PIN.
    /// </summary>
    public static byte[] ToPinBytes(ulong address)
    {
        var pin = new byte[6];
        for (int i = 0; i < 6; i++) pin[i] = (byte)(address >> (8 * i));
        return pin;
    }
}

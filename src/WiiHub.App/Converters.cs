using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;

namespace WiiHub.App;

/// <summary>true → <see cref="On"/>, false → <see cref="Off"/>.</summary>
public sealed class BoolToBrushConverter : IValueConverter
{
    public Brush On { get; set; } = Brushes.DodgerBlue;
    public Brush Off { get; set; } = Brushes.LightGray;

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is true ? On : Off;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>true → <see cref="On"/> opacity, false → <see cref="Off"/>.</summary>
public sealed class BoolToOpacityConverter : IValueConverter
{
    public double On { get; set; } = 1.0;
    public double Off { get; set; } = 0.22;

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is true ? On : Off;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>true → Visible (or Collapsed when <see cref="Invert"/> is set).</summary>
public sealed class BoolToVisibilityConverter : IValueConverter
{
    public bool Invert { get; set; }

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        (value is true) != Invert ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>
/// Maps a stick value (-1..1) to a pixel offset in 0..range. The ConverterParameter is the range;
/// a negative range flips the direction (for Y, where up is positive but screen Y grows down).
/// </summary>
public sealed class AxisToOffsetConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        double v = value is double d ? Math.Clamp(d, -1, 1) : 0;
        double range = double.TryParse(parameter as string, NumberStyles.Float, CultureInfo.InvariantCulture, out var r) ? r : 50;
        double t = (v + 1) / 2;
        return range < 0 ? (1 - t) * -range : t * range;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>
/// "#RRGGBB" string to a (cached, frozen) brush. An optional ConverterParameter is the opacity
/// (0..1), e.g. a light tint of a fret colour that works on both light and dark backgrounds.
/// Unknown values give a transparent brush.
/// </summary>
public sealed class HexToBrushConverter : IValueConverter
{
    private static readonly Dictionary<string, Brush> Cache = new(StringComparer.OrdinalIgnoreCase);

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is not string hex || string.IsNullOrWhiteSpace(hex)) return Brushes.Transparent;
        double opacity = 1;
        if (parameter is string p && double.TryParse(p, NumberStyles.Float, CultureInfo.InvariantCulture, out var o))
            opacity = Math.Clamp(o, 0, 1);

        string key = $"{hex}|{opacity:0.###}";
        lock (Cache)
        {
            if (Cache.TryGetValue(key, out var cached)) return cached;
            try
            {
                var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex)) { Opacity = opacity };
                brush.Freeze();
                Cache[key] = brush;
                return brush;
            }
            catch (FormatException)
            {
                return Brushes.Transparent;
            }
        }
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>
/// Battery percentage (0..100, or -1 when unknown) to a battery glyph from the Windows icon font
/// (Segoe Fluent Icons / MDL2: Battery0 U+E850 … Battery10 U+E85A).
/// </summary>
public sealed class BatteryToGlyphConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is not int p || p < 0) return string.Empty;
        int step = (int)Math.Round(Math.Clamp(p, 0, 100) / 10.0); // 0..10
        return ((char)(0xE850 + step)).ToString();
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>
/// Signal bars: value = number of lit bars, ConverterParameter = this bar's number (1-based).
/// Returns full opacity for lit bars and a faint one for the others.
/// </summary>
public sealed class IndexOpacityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        int lit = value is int v ? v : 0;
        int index = parameter is string p && int.TryParse(p, NumberStyles.Integer, CultureInfo.InvariantCulture, out var i) ? i : 1;
        return lit >= index ? 1.0 : 0.25;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>Battery percentage (or -1 when unknown) to a colour.</summary>
public sealed class BatteryToBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value switch
    {
        int p when p < 0 => Brushes.Transparent,
        int p when p <= 20 => new SolidColorBrush(Color.FromRgb(0xE5, 0x48, 0x4D)),
        int p when p <= 45 => new SolidColorBrush(Color.FromRgb(0xF2, 0xA5, 0x3A)),
        _ => new SolidColorBrush(Color.FromRgb(0x2E, 0xB8, 0x72)),
    };

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

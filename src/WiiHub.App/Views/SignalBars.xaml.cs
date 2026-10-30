using System.Windows;
using System.Windows.Controls;

namespace WiiHub.App.Views;

public partial class SignalBars : UserControl
{
    public static readonly DependencyProperty BarsProperty =
        DependencyProperty.Register(nameof(Bars), typeof(int), typeof(SignalBars), new PropertyMetadata(0));

    public static readonly DependencyProperty ColorProperty =
        DependencyProperty.Register(nameof(Color), typeof(string), typeof(SignalBars), new PropertyMetadata("#8A94A6"));

    public SignalBars()
    {
        InitializeComponent();
    }

    /// <summary>Number of lit bars, 0..4.</summary>
    public int Bars
    {
        get => (int)GetValue(BarsProperty);
        set => SetValue(BarsProperty, value);
    }

    /// <summary>Colour of the lit bars as "#RRGGBB".</summary>
    public string Color
    {
        get => (string)GetValue(ColorProperty);
        set => SetValue(ColorProperty, value);
    }
}

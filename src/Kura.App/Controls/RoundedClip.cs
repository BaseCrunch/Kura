using System.Windows;
using System.Windows.Media;

namespace Kura.App.Controls;

/// <summary>Clips an element to a rounded rectangle that follows its size (cheaper than an OpacityMask per cell).</summary>
public static class RoundedClip
{
    public static readonly DependencyProperty RadiusProperty = DependencyProperty.RegisterAttached(
        "Radius", typeof(double), typeof(RoundedClip), new PropertyMetadata(0.0, OnRadiusChanged));

    public static double GetRadius(DependencyObject o) => (double)o.GetValue(RadiusProperty);
    public static void SetRadius(DependencyObject o, double v) => o.SetValue(RadiusProperty, v);

    private static void OnRadiusChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not FrameworkElement fe) return;
        fe.SizeChanged -= Update;
        fe.SizeChanged += Update;
        Apply(fe);
    }

    private static void Update(object sender, SizeChangedEventArgs e) => Apply((FrameworkElement)sender);

    private static void Apply(FrameworkElement fe)
    {
        var r = GetRadius(fe);
        var g = new RectangleGeometry(new Rect(0, 0, fe.ActualWidth, fe.ActualHeight), r, r);
        g.Freeze();
        fe.Clip = g;
    }
}

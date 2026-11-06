using System.Windows;
using System.Windows.Media;

namespace EchoDeck;

/// <summary>
/// Draws the four rounded corners over a rectangle in the window colour. The video picture is a native surface that
/// WPF can't clip, so the video overlay uses this to make the picture look rounded.
/// </summary>
public sealed class CornerMask : FrameworkElement
{
    public static readonly DependencyProperty RadiusProperty =
        DependencyProperty.Register(nameof(Radius), typeof(double), typeof(CornerMask),
            new FrameworkPropertyMetadata(14.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public double Radius
    {
        get => (double)GetValue(RadiusProperty);
        set => SetValue(RadiusProperty, value);
    }

    public CornerMask()
    {
        IsHitTestVisible = false;
    }

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight, r = Radius;
        if (w < 2 * r + 2 || h < 2 * r + 2 || r <= 0) return;

        var brush = TryFindResource("BgBrush") as Brush ?? Brushes.Black;
        var outer = new RectangleGeometry(new Rect(0, 0, w, h));
        var inner = new RectangleGeometry(new Rect(0, 0, w, h), r, r);
        dc.DrawGeometry(brush, null, new CombinedGeometry(GeometryCombineMode.Exclude, outer, inner));
    }
}

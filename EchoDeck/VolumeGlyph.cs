using System.Windows;
using System.Windows.Media;

namespace EchoDeck;

/// <summary>
/// The speaker icon of the volume button. The sound waves appear and disappear with the level (one wave up to a third, two
/// above), a cross pops in when muted or at 0 %, and the waves pulse every time the volume is changed.
/// Drawn in a 24 x 24 space, like the other icons.
/// </summary>
public sealed class VolumeGlyph : FrameworkElement
{
    private static readonly Geometry Body = Make("M 3,9.2 L 7,9.2 L 12,5 L 12,19 L 7,14.8 L 3,14.8 Z");
    private static readonly Geometry WaveNear = Make("M 14.8,9.4 C 16,10.4 16,13.6 14.8,14.6 L 16,16.2 C 18.2,14.4 18.2,9.6 16,7.8 Z");
    private static readonly Geometry WaveFar = Make("M 18,6 C 21.4,8.8 21.4,15.2 18,18 L 19.2,19.6 C 23.4,16.2 23.4,7.8 19.2,4.4 Z");

    public static readonly DependencyProperty ForegroundProperty =
        DependencyProperty.Register(nameof(Foreground), typeof(Brush), typeof(VolumeGlyph),
            new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    private readonly object _keyNear = new(), _keyFar = new(), _keyCross = new(), _keyPulse = new();
    private double _near = 1, _far = 1, _cross, _pulse;
    private double _level = -1;

    public VolumeGlyph()
    {
        Width = 24;
        Height = 24;
        IsHitTestVisible = false;
    }

    public Brush? Foreground
    {
        get => (Brush?)GetValue(ForegroundProperty);
        set => SetValue(ForegroundProperty, value);
    }

    /// <param name="level">0..1</param>
    public void SetState(double level, bool muted, bool animate)
    {
        bool cross = muted || level <= 0.0001;
        double near = cross ? 0 : 1;
        double far = cross || level <= 0.34 ? 0 : 1;
        bool known = _level >= 0;
        bool changed = known && Math.Abs(level - _level) > 0.0001;
        _level = level;

        int ms = animate && known ? 200 : 0;
        Go(_keyNear, _near, near, ms, v => _near = v);
        Go(_keyFar, _far, far, ms, v => _far = v);
        Go(_keyCross, _cross, cross ? 1 : 0, ms, v => _cross = v);

        if (animate && known && changed && !cross)
            UiAnim.Tween(_keyPulse, 1, 0, 280, v => { _pulse = v; InvalidateVisual(); });
    }

    private void Go(object key, double from, double to, int ms, Action<double> set) =>
        UiAnim.Tween(key, from, to, ms, v => { set(v); InvalidateVisual(); });

    protected override void OnRender(DrawingContext dc)
    {
        var brush = Foreground ?? Brushes.Gray;
        dc.DrawGeometry(brush, null, Body);
        DrawWave(dc, brush, WaveNear, _near);
        DrawWave(dc, brush, WaveFar, _far);

        if (_cross > 0.001)
        {
            var pen = new Pen(brush, 1.9) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
            var m = Matrix.Identity;
            double s = 0.55 + 0.45 * _cross;
            m.ScaleAt(s, s, 18, 12);
            dc.PushOpacity(_cross);
            dc.PushTransform(new MatrixTransform(m));
            dc.DrawLine(pen, new Point(15.2, 9.2), new Point(20.8, 14.8));
            dc.DrawLine(pen, new Point(20.8, 9.2), new Point(15.2, 14.8));
            dc.Pop();
            dc.Pop();
        }
    }

    private void DrawWave(DrawingContext dc, Brush brush, Geometry wave, double amount)
    {
        if (amount <= 0.001) return;

        double scale = (0.8 + 0.2 * amount) * (1 + 0.14 * _pulse);
        var m = Matrix.Identity;
        m.ScaleAt(scale, scale, 13, 12);
        m.Translate(-2.5 * (1 - amount), 0);

        dc.PushOpacity(amount);
        dc.PushTransform(new MatrixTransform(m));
        dc.DrawGeometry(brush, null, wave);
        dc.Pop();
        dc.Pop();
    }

    private static Geometry Make(string data)
    {
        var g = Geometry.Parse(data);
        g.Freeze();
        return g;
    }
}

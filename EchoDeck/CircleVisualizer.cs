using System.Diagnostics;
using System.Windows;
using System.Windows.Media;

namespace EchoDeck;

/// <summary>
/// Music-view visualiser drawn around the round cover. Mode 1 = radial bars, mode 2 = wave ring (style 0, the ripples,
/// is separate XAML). It follows the song's own waveform peaks at the current position, so it really moves with the music,
/// and settles down when playback pauses.
/// </summary>
public sealed class CircleVisualizer : FrameworkElement, IDisposable
{
    private bool _disposed;
    private const int BarCount = 72;       // 36 per side, mirrored left / right
    private const double InnerRadius = 164;
    private const double MaxBar = 34;

    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly Stopwatch _sincePosition = Stopwatch.StartNew();
    private float[]? _peaks;
    private double _fraction;
    private double _lengthSeconds;
    private bool _playing;
    private double _level = 0.1;   // eased 0.1 (idle) .. 1 (playing)
    private int _style;
    private bool _subscribed;
    private double _lastRender;
    private readonly double[] _smooth = new double[BarCount / 2 + 1];

    public CircleVisualizer()
    {
        IsHitTestVisible = false;
        Loaded += (_, _) => Sync();
        Unloaded += (_, _) => Unsubscribe();
        IsVisibleChanged += (_, _) => Sync();
    }

    /// <summary>The colour of the visualiser (the cover's own colour); null = the accent colour.</summary>
    public Brush? Tint { get; set; }

    /// <summary>0 = off (ripples are used), 1 = radial bars, 2 = wave ring. (Not called "Style": that name belongs to WPF.)</summary>
    public int Mode
    {
        get => _style;
        set { _style = value; Sync(); }
    }

    public void SetPeaks(float[]? peaks) => _peaks = peaks;

    public bool IsPlaying
    {
        get => _playing;
        set { _playing = value; Sync(); }
    }

    public void SetPosition(double fraction, double lengthSeconds)
    {
        _fraction = Math.Clamp(fraction, 0, 1);
        _lengthSeconds = lengthSeconds;
        _sincePosition.Restart();
    }

    private void Sync()
    {
        if (!_disposed && _style > 0 && IsLoaded && IsVisible) Subscribe(); else { Unsubscribe(); }
        if (!_disposed) InvalidateVisual();
    }

    /// <summary>Stops the frame loop and drops the waveform: called when the window closes or the music view is torn down.</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Unsubscribe();
        _peaks = null;
        _playing = false;
        Tint = null;
    }

    /// <summary>Back to rest between songs: no waveform, no motion (the frame loop itself stays tied to visibility).</summary>
    public void Reset()
    {
        _peaks = null;
        _playing = false;
        _fraction = 0;
        _lengthSeconds = 0;
        Array.Clear(_smooth, 0, _smooth.Length);
    }

    private void Subscribe()
    {
        if (_subscribed) return;
        _subscribed = true;
        CompositionTarget.Rendering += OnFrame;
    }

    private void Unsubscribe()
    {
        if (!_subscribed) return;
        _subscribed = false;
        CompositionTarget.Rendering -= OnFrame;
    }

    private void OnFrame(object? sender, EventArgs e)
    {
        if (_disposed) { Unsubscribe(); return; }

        // ~40 fps is plenty for this and keeps the CPU quiet
        double now = _clock.Elapsed.TotalSeconds;
        if (now - _lastRender < 0.025) return;
        _lastRender = now;
        InvalidateVisual();
    }

    /// <summary>Peak height 0..1 around a point in the song (bucket-interpolated).</summary>
    private double PeakAt(double fraction, double t, int salt)
    {
        var peaks = _peaks;
        if (peaks == null || peaks.Length < 2)
        {
            // no waveform (yet): gentle synthetic movement
            return 0.35 + 0.3 * Math.Sin(t * 2.3 + salt * 0.7) * Math.Sin(t * 1.1 + salt * 0.31) + 0.15 * Math.Sin(t * 5.7 + salt);
        }

        double pos = Math.Clamp(fraction, 0, 1) * (peaks.Length - 1);
        int i0 = (int)Math.Floor(pos);
        int i1 = Math.Min(peaks.Length - 1, i0 + 1);
        double f = pos - i0;
        double v = peaks[i0] * (1 - f) + peaks[i1] * f;
        return Math.Clamp(v, 0, 1);
    }

    protected override void OnRender(DrawingContext dc)
    {
        if (_disposed || _style <= 0 || ActualWidth <= 0) return;

        try
        {
            Draw(dc);
        }
        catch (Exception ex)
        {
            // a drawing hiccup (for example while the window is closing) must never become a crash
            Unsubscribe();
            AppLog.Write("Visualiser", ex);
        }
    }

    private void Draw(DrawingContext dc)
    {

        double t = _clock.Elapsed.TotalSeconds;
        double frac = _fraction;
        if (_playing && _lengthSeconds > 0) frac = Math.Min(1, _fraction + _sincePosition.Elapsed.TotalSeconds / _lengthSeconds);

        _level += ((_playing ? 1.0 : 0.1) - _level) * 0.08;

        // how far one bar steps through the song (a few seconds across the whole half ring)
        double span = _lengthSeconds > 0 ? Math.Min(0.25, 14.0 / _lengthSeconds) : 0.05;
        int half = BarCount / 2;
        for (int d = 0; d <= half; d++)
        {
            double target = PeakAt(frac + span * d / half, t, d);
            // a little life on top so quiet passages still breathe
            target *= 0.8 + 0.2 * Math.Sin(t * 7.5 + d * 0.8);
            _smooth[d] += (target - _smooth[d]) * 0.35;
        }

        var brush = Tint ?? TryFindResource("AccentBrush") as Brush ?? Brushes.White;
        var center = new Point(ActualWidth / 2, ActualHeight / 2);

        if (_style == 1) DrawBars(dc, brush, center);
        else DrawRing(dc, brush, center);
    }

    private void DrawBars(DrawingContext dc, Brush brush, Point c)
    {
        var pen = new Pen(brush, 4.2) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
        if (pen.CanFreeze) pen.Freeze();

        for (int i = 0; i < BarCount; i++)
        {
            // bar 0 sits at the top; left and right halves mirror each other
            int d = i <= BarCount / 2 ? i : BarCount - i;
            double angle = 2 * Math.PI * i / BarCount - Math.PI / 2;
            double len = 3 + MaxBar * _smooth[d] * _level;

            double cos = Math.Cos(angle), sin = Math.Sin(angle);
            dc.DrawLine(pen,
                new Point(c.X + cos * InnerRadius, c.Y + sin * InnerRadius),
                new Point(c.X + cos * (InnerRadius + len), c.Y + sin * (InnerRadius + len)));
        }
    }

    private void DrawRing(DrawingContext dc, Brush brush, Point c)
    {
        const int points = 180;
        double baseR = InnerRadius + 8;

        var geo = new StreamGeometry();
        using (var ctx = geo.Open())
        {
            for (int i = 0; i <= points; i++)
            {
                double u = (double)i / points;               // 0..1 round the ring
                double angle = 2 * Math.PI * u - Math.PI / 2;
                // mirrored: 0 at the top .. 1 at the bottom, then back
                double m = u <= 0.5 ? u * 2 : (1 - u) * 2;
                double pos = m * (BarCount / 2);
                int lo = (int)Math.Floor(pos);
                int hi = Math.Min(BarCount / 2, lo + 1);
                double a = _smooth[lo] * (1 - (pos - lo)) + _smooth[hi] * (pos - lo);

                double r = baseR + (a * _level - 0.25) * MaxBar * 0.9;
                var pt = new Point(c.X + Math.Cos(angle) * r, c.Y + Math.Sin(angle) * r);
                if (i == 0) ctx.BeginFigure(pt, false, true); else ctx.LineTo(pt, true, true);
            }
        }
        geo.Freeze();

        var thick = new Pen(brush, 3.2) { LineJoin = PenLineJoin.Round };
        var thin = new Pen(brush, 1.4) { LineJoin = PenLineJoin.Round };
        dc.PushOpacity(0.28);
        dc.DrawEllipse(null, thin, c, baseR + 14, baseR + 14);
        dc.Pop();
        dc.DrawGeometry(null, thick, geo);
    }
}

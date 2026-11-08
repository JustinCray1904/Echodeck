using System.Diagnostics;
using System.Windows;
using System.Windows.Media;

namespace EchoDeck;

/// <summary>
/// Music-view visualiser drawn around the round cover. Mode 1 = radial bars, mode 2 = aura (style 0, the ripples, is separate
/// XAML). It shows the song's real spectrum (bass .. treble, analysed from the file at the current position): each band rises
/// quickly and falls slowly, the bass makes the bars and the cover breathe, and the colours drift from the cover's own colour.
/// Songs whose spectrum is not available (very long mixes) fall back to the waveform level.
/// </summary>
public sealed class CircleVisualizer : FrameworkElement, IDisposable
{
    private bool _disposed;
    private const int Half = 48;                  // bars per side; mirrored left / right, bass at the bottom, treble at the top
    private const int BarCount = Half * 2;
    private const double InnerRadius = 166;       // the cover is 155: a clear gap between cover and visualiser
    private const int HistoryLength = 10;

    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly Stopwatch _sincePosition = Stopwatch.StartNew();
    private SpectrumData? _spectrum;
    private float[]? _peaks;
    private double _fraction;
    private double _lengthSeconds;
    private bool _playing;
    private double _level = 0.1;                  // eased 0.1 (idle) .. 1 (playing)
    private int _style;
    private bool _subscribed;
    private double _lastRender;
    private double _lastFrameTime;
    private double _pulse;
    private readonly double[] _bands = new double[Half + 1];      // smoothed, 0..1, index 0 = bass
    private readonly double[] _shaped = new double[Half + 3];     // spatially smoothed copy (with padding)
    private readonly double[][] _history = new double[HistoryLength][];   // recent frames: the aura's trailing echoes
    private int _historyPos;

    // colours are prepared once per tint, not per frame
    private Color _paletteTint;
    private Pen[]? _barCore, _barGlow;
    private Pen? _stillRing;

    public CircleVisualizer()
    {
        IsHitTestVisible = false;
        for (int i = 0; i < HistoryLength; i++) _history[i] = new double[Half + 3];
        Loaded += (_, _) => Sync();
        Unloaded += (_, _) => Unsubscribe();
        IsVisibleChanged += (_, _) => Sync();
    }

    /// <summary>How tall / bold the bars and the aura move (Settings > Interface). 1 = default.</summary>
    public double Intensity { get; set; } = 1.0;

    /// <summary>How strongly it reacts to the music: low ignores quiet parts, high lifts them. 1 = default.</summary>
    public double Sensitivity { get; set; } = 1.0;

    /// <summary>The colour of the visualiser (the cover's own colour); null = the accent colour.</summary>
    public Brush? Tint { get; set; }

    /// <summary>The cover's scale transform: it swells a little with the bass.</summary>
    public ScaleTransform? CoverPulse { get; set; }

    /// <summary>True while the window layout is changing: no frames are drawn (keeps the overlay window quiet).</summary>
    public bool Frozen { get; set; }

    /// <summary>0 = off (ripples are used), 1 = radial bars, 2 = aura. (Not called "Style": that name belongs to WPF.)</summary>
    public int Mode
    {
        get => _style;
        set { _style = value; Sync(); }
    }

    public void SetPeaks(float[]? peaks) => _peaks = peaks;

    internal void SetSpectrum(SpectrumData? spectrum) => _spectrum = spectrum;

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

    /// <summary>Stops the frame loop and drops the analysis: called when the window closes or the music view is torn down.</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Unsubscribe();
        _peaks = null;
        _spectrum = null;
        _playing = false;
        Tint = null;
        CoverPulse = null;
    }

    /// <summary>Back to rest between songs: no analysis, no motion (the frame loop itself stays tied to visibility).</summary>
    public void Reset()
    {
        _peaks = null;
        _spectrum = null;
        _playing = false;
        _fraction = 0;
        _lengthSeconds = 0;
        Array.Clear(_bands, 0, _bands.Length);
        if (CoverPulse != null) { CoverPulse.ScaleX = 1; CoverPulse.ScaleY = 1; }
        _pulse = 0;
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
        if (Frozen) return;

        // 30 fps matches the analysis and keeps the CPU quiet
        double now = _clock.Elapsed.TotalSeconds;
        if (now - _lastRender < 0.030) return;
        _lastRender = now;
        InvalidateVisual();
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

    /// <summary>Current band value 0..1 from the spectrum (linear blend of the two nearest analysis frames).</summary>
    private double SpectrumAt(double seconds, int band)
    {
        var s = _spectrum!;
        double pos = Math.Clamp(seconds * SpectrumData.FramesPerSecond, 0, s.Frames - 1.001);
        int f0 = (int)pos;
        double t = pos - f0;
        int i0 = f0 * SpectrumData.Bands + band;
        int i1 = Math.Min(s.Frames - 1, f0 + 1) * SpectrumData.Bands + band;
        return (s.Values[i0] * (1 - t) + s.Values[i1] * t) / 255.0;
    }

    /// <summary>Without a spectrum: the waveform level at this point, shaped so the bass end moves most.</summary>
    private double FallbackAt(double fraction, double time, int d)
    {
        double level = 0.35;
        var peaks = _peaks;
        if (peaks is { Length: > 1 })
        {
            double pos = Math.Clamp(fraction, 0, 1) * (peaks.Length - 1);
            int i0 = (int)Math.Floor(pos);
            int i1 = Math.Min(peaks.Length - 1, i0 + 1);
            level = peaks[i0] * (1 - (pos - i0)) + peaks[i1] * (pos - i0);
        }
        double shape = 1.0 - 0.55 * d / Half;
        double wobble = 0.6 + 0.4 * Math.Sin(time * 6.0 + d * 0.9) * Math.Sin(time * 2.3 + d * 0.35);
        return Math.Clamp(level * shape * wobble, 0, 1);
    }

    private void Draw(DrawingContext dc)
    {
        double t = _clock.Elapsed.TotalSeconds;
        double dt = Math.Clamp(t - _lastFrameTime, 0.001, 0.1);
        _lastFrameTime = t;

        double frac = _fraction;
        if (_playing && _lengthSeconds > 0) frac = Math.Min(1, _fraction + _sincePosition.Elapsed.TotalSeconds / _lengthSeconds);
        double seconds = frac * _lengthSeconds + 0.04;   // a touch ahead: the sound takes a moment to reach the speakers

        _level += ((_playing ? 1.0 : 0.0) - _level) * Math.Min(1, dt * 5);

        // the two sliders in Settings > Interface are read live, so moving them changes the picture on the spot
        double sensitivity = Math.Clamp(AppSettings.Current.VisualizerSensitivity, 0.4, 2.5);
        double intensity = Math.Clamp(AppSettings.Current.VisualizerIntensity, 0.4, 2.0);
        double gamma = 1.0 / sensitivity;     // high sensitivity lifts quiet parts, low sensitivity keeps only the loud ones

        bool haveSpectrum = _spectrum != null && _lengthSeconds > 0;
        double attack = 1 - Math.Exp(-dt / 0.04);      // rises within a couple of frames
        double release = 1 - Math.Exp(-dt / 0.22);     // falls gently

        for (int d = 0; d <= Half; d++)
        {
            double target;
            if (!_playing) target = 0;
            else if (haveSpectrum)
            {
                // d = 0 is the bass (bottom of the ring); each position blends two neighbouring bands
                double u = (double)d / Half * (SpectrumData.Bands - 1);
                int b0 = (int)Math.Floor(u);
                int b1 = Math.Min(SpectrumData.Bands - 1, b0 + 1);
                double v = SpectrumAt(seconds, b0) * (1 - (u - b0)) + SpectrumAt(seconds, b1) * (u - b0);
                target = Math.Pow(Math.Clamp(v, 0, 1), gamma * 1.1);
            }
            else
            {
                target = Math.Pow(FallbackAt(frac, t, d), gamma);
            }

            _bands[d] += (target - _bands[d]) * (target > _bands[d] ? attack : release);
        }

        // spatial smoothing so neighbouring bars flow into each other (mirrored at both ends: no seam)
        for (int d = 0; d <= Half; d++)
        {
            double prev = _bands[Math.Max(0, d - 1)];
            double next = _bands[Math.Min(Half, d + 1)];
            _shaped[d + 1] = prev * 0.25 + _bands[d] * 0.5 + next * 0.25;
        }
        _shaped[0] = _shaped[2];
        _shaped[Half + 2] = _shaped[Half];

        // remember this frame for the aura's echoes
        _history[_historyPos] = (double[])_shaped.Clone();
        _historyPos = (_historyPos + 1) % HistoryLength;

        double bass = (_bands[0] + _bands[1] + _bands[2] + _bands[3] + _bands[4]) / 5;
        double live = 0.2 + 0.8 * _level;

        // the cover swells a little with the bass
        if (CoverPulse != null)
        {
            _pulse += (bass * live * Math.Min(intensity, 1.5) - _pulse) * 0.35;
            double scale = 1 + 0.032 * _pulse;
            CoverPulse.ScaleX = scale;
            CoverPulse.ScaleY = scale;
        }

        var center = new Point(ActualWidth / 2, ActualHeight / 2);
        if (_style == 1) DrawBars(dc, center, intensity, bass, live);
        else DrawAura(dc, center, intensity, live);
    }

    private static double At(double[] arr, int d) => arr[Math.Clamp(d, 0, Half) + 1];

    private double[] Delayed(int frames) => _history[(_historyPos - 1 - frames + 2 * HistoryLength) % HistoryLength];

    // ---------------------------------------------------------------- colours

    private Color TintColor()
    {
        if (Tint is SolidColorBrush own) return own.Color;
        if (TryFindResource("AccentBrush") is SolidColorBrush accent) return accent.Color;
        return Colors.White;
    }

    private static SolidColorBrush Solid(Color c, double alpha)
    {
        var b = new SolidColorBrush(Color.FromArgb((byte)Math.Clamp((int)Math.Round(alpha * 255), 0, 255), c.R, c.G, c.B));
        b.Freeze();
        return b;
    }

    /// <summary>Rotates the hue (degrees) and scales saturation / brightness: the colours drift gently away from the cover's colour.</summary>
    private static Color HueShift(Color c, double degrees, double satMul, double valMul)
    {
        double r = c.R / 255.0, g = c.G / 255.0, b = c.B / 255.0;
        double max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b)), delta = max - min;
        double h = 0;
        if (delta > 1e-9)
        {
            if (max == r) h = ((g - b) / delta) % 6;
            else if (max == g) h = (b - r) / delta + 2;
            else h = (r - g) / delta + 4;
            h *= 60;
            if (h < 0) h += 360;
        }
        double s = max <= 0 ? 0 : delta / max;
        double v = max;

        h = (h + degrees) % 360;
        if (h < 0) h += 360;
        s = Math.Min(1, s * satMul);
        v = Math.Min(1, v * valMul);

        double cc = v * s;
        double x = cc * (1 - Math.Abs((h / 60) % 2 - 1));
        double m = v - cc;
        double r1, g1, b1;
        switch ((int)(h / 60) % 6)
        {
            case 0: r1 = cc; g1 = x; b1 = 0; break;
            case 1: r1 = x; g1 = cc; b1 = 0; break;
            case 2: r1 = 0; g1 = cc; b1 = x; break;
            case 3: r1 = 0; g1 = x; b1 = cc; break;
            case 4: r1 = x; g1 = 0; b1 = cc; break;
            default: r1 = cc; g1 = 0; b1 = x; break;
        }
        return Color.FromRgb((byte)Math.Round((r1 + m) * 255), (byte)Math.Round((g1 + m) * 255), (byte)Math.Round((b1 + m) * 255));
    }

    private void EnsureBarPalette(Color tint)
    {
        if (_barCore != null && tint == _paletteTint) return;
        _paletteTint = tint;
        _barCore = new Pen[Half + 1];
        _barGlow = new Pen[Half + 1];
        for (int d = 0; d <= Half; d++)
        {
            var col = HueShift(tint, -18 + 36.0 * d / Half, 1.15, 1.1);   // bass .. treble
            _barCore[d] = new Pen(Solid(col, 1.0), 3.6) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
            _barGlow[d] = new Pen(Solid(col, 0.18), 9) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
            _barCore[d].Freeze();
            _barGlow[d].Freeze();
        }
        _stillRing = new Pen(Solid(tint, 0.28), 1.2);
        _stillRing.Freeze();
    }

    // ---------------------------------------------------------------- bars

    /// <summary>
    /// Radial bars, the way spectrum analysers draw them: bars grow outward from a circle that swells with the bass, bass at the
    /// bottom, mirrored left and right, each with a soft glow.
    /// </summary>
    private void DrawBars(DrawingContext dc, Point c, double intensity, double bass, double live)
    {
        EnsureBarPalette(TintColor());
        double r0 = InnerRadius + bass * live * 5;

        for (int i = 0; i < BarCount; i++)
        {
            int d = i <= Half ? i : BarCount - i;                  // mirrored: bar 0 is the bass at the bottom
            double angle = Math.PI / 2 + 2 * Math.PI * i / BarCount;
            double cos = Math.Cos(angle), sin = Math.Sin(angle);

            double len = 4 + 44 * Math.Min(1.0, At(_shaped, d) * intensity * live);
            var p0 = new Point(c.X + cos * r0, c.Y + sin * r0);
            var p1 = new Point(c.X + cos * (r0 + len), c.Y + sin * (r0 + len));
            dc.DrawLine(_barGlow![d], p0, p1);
            dc.DrawLine(_barCore![d], p0, p1);
        }

        dc.DrawEllipse(null, _stillRing, c, InnerRadius - 7, InnerRadius - 7);
    }

    // ---------------------------------------------------------------- aura

    /// <summary>
    /// A glowing, filled shape around the cover: always outside the cover, smooth, symmetrical; two fainter copies of the last
    /// moments trail behind it, so the shape seems to flow with the music.
    /// </summary>
    private void DrawAura(DrawingContext dc, Point c, double intensity, double live)
    {
        var tint = TintColor();
        DrawAuraLayer(dc, c, Delayed(6), HueShift(tint, 22, 1.1, 1.05), 0.30, 1.4, false, intensity, live);
        DrawAuraLayer(dc, c, Delayed(3), HueShift(tint, 11, 1.1, 1.05), 0.55, 1.8, false, intensity, live);
        DrawAuraLayer(dc, c, _shaped, HueShift(tint, 0, 1.15, 1.1), 1.0, 2.6, true, intensity, live);
    }

    private static void DrawAuraLayer(DrawingContext dc, Point c, double[] arr, Color col, double alpha, double width,
        bool filled, double intensity, double live)
    {
        const int n = 192;

        // the level round the ring (mirrored: bass at the bottom), blurred along the ring so it flows
        var ring = new double[n];
        for (int j = 0; j < n; j++)
        {
            double u = (double)j / n;
            double m = (u <= 0.5 ? u * 2 : (1 - u) * 2) * Half;
            int lo = (int)Math.Floor(m);
            ring[j] = Cubic(At(arr, lo - 1), At(arr, lo), At(arr, lo + 1), At(arr, lo + 2), m - lo);
        }
        var tmp = new double[n];
        for (int pass = 0; pass < 3; pass++)
        {
            for (int j = 0; j < n; j++)
                tmp[j] = ring[(j + n - 1) % n] * 0.25 + ring[j] * 0.5 + ring[(j + 1) % n] * 0.25;
            Array.Copy(tmp, ring, n);
        }

        var pts = new Point[n];
        for (int j = 0; j < n; j++)
        {
            double angle = Math.PI / 2 + 2 * Math.PI * j / n;
            double r = InnerRadius + 5 + 44 * intensity * live * Math.Clamp(ring[j], 0, 1.05);
            pts[j] = new Point(c.X + Math.Cos(angle) * r, c.Y + Math.Sin(angle) * r);
        }

        var geo = new StreamGeometry();
        using (var ctx = geo.Open())
        {
            // quadratic curves through the midpoints: a smooth closed line, no corners
            ctx.BeginFigure(Mid(pts[n - 1], pts[0]), false, true);
            for (int j = 0; j < n; j++)
                ctx.QuadraticBezierTo(pts[j], Mid(pts[j], pts[(j + 1) % n]), true, true);
        }
        geo.Freeze();

        if (filled)
        {
            // the area between the cover's circle and the shape (even-odd: the circle punches the hole)
            var area = new GeometryGroup { FillRule = FillRule.EvenOdd };
            area.Children.Add(geo);
            area.Children.Add(new EllipseGeometry(c, InnerRadius, InnerRadius));
            dc.DrawGeometry(Solid(col, 0.27), null, area);
        }

        var glow = new Pen(Solid(col, 0.22 * alpha), 9) { LineJoin = PenLineJoin.Round };
        var core = new Pen(Solid(col, alpha), width) { LineJoin = PenLineJoin.Round };
        dc.DrawGeometry(null, glow, geo);
        dc.DrawGeometry(null, core, geo);
    }

    private static Point Mid(Point a, Point b) => new((a.X + b.X) / 2, (a.Y + b.Y) / 2);

    /// <summary>Catmull-Rom curve through four neighbouring values.</summary>
    private static double Cubic(double p0, double p1, double p2, double p3, double t) =>
        p1 + 0.5 * t * (p2 - p0 + t * (2 * p0 - 5 * p1 + 4 * p2 - p3 + t * (3 * (p1 - p2) + p3 - p0)));
}

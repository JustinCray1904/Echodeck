using System.Diagnostics;
using System.Windows;
using System.Windows.Media;

namespace EchoDeck;

/// <summary>
/// Music-view visualiser drawn around the round cover. Mode 1 = radial bars, mode 2 = wave ring (style 0, the ripples, is
/// separate XAML). It shows the song's real spectrum (bass .. treble, analysed from the file at the current position), so it
/// moves with the music; each band rises quickly and falls slowly, and the shape is smoothed so it never looks jagged.
/// Songs whose spectrum is not available (very long mixes) fall back to the waveform level.
/// </summary>
public sealed class CircleVisualizer : FrameworkElement, IDisposable
{
    private bool _disposed;
    private const int BarCount = 72;              // 36 per side, mirrored left / right (bass at the bottom, treble at the top)
    private const int Half = BarCount / 2;
    private const double InnerRadius = 164;
    private const double MaxBar = 34;

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
    private readonly double[] _bands = new double[Half + 1];     // smoothed, 0..1, index 0 = bass
    private readonly double[] _shaped = new double[Half + 3];    // spatially smoothed copy (with padding for interpolation)

    public CircleVisualizer()
    {
        IsHitTestVisible = false;
        Loaded += (_, _) => Sync();
        Unloaded += (_, _) => Unsubscribe();
        IsVisibleChanged += (_, _) => Sync();
    }

    /// <summary>How tall / bold the bars and the ring move (Settings > Interface). 1 = default.</summary>
    public double Intensity { get; set; } = 1.0;

    /// <summary>How strongly it reacts to the music: low ignores quiet parts, high lifts them. 1 = default.</summary>
    public double Sensitivity { get; set; } = 1.0;

    /// <summary>The colour of the visualiser (the cover's own colour); null = the accent colour.</summary>
    public Brush? Tint { get; set; }

    /// <summary>True while the window layout is changing: no frames are drawn (keeps the overlay window quiet).</summary>
    public bool Frozen { get; set; }

    /// <summary>0 = off (ripples are used), 1 = radial bars, 2 = wave ring. (Not called "Style": that name belongs to WPF.)</summary>
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
        double attack = 1 - Math.Exp(-dt / 0.045);     // rises within a few frames
        double release = 1 - Math.Exp(-dt / 0.20);     // falls gently

        for (int d = 0; d <= Half; d++)
        {
            double target;
            if (!_playing) target = 0;
            else if (haveSpectrum)
            {
                // d = 0 is the bass (bottom of the ring); each ring position averages two neighbouring bands
                double u = (double)d / Half * (SpectrumData.Bands - 1);
                int b0 = (int)Math.Floor(u);
                int b1 = Math.Min(SpectrumData.Bands - 1, b0 + 1);
                double v = SpectrumAt(seconds, b0) * (1 - (u - b0)) + SpectrumAt(seconds, b1) * (u - b0);
                target = Math.Pow(Math.Clamp(v, 0, 1), gamma * 1.15);
            }
            else
            {
                target = Math.Pow(FallbackAt(frac, t, d), gamma);
            }

            _bands[d] += (target - _bands[d]) * (target > _bands[d] ? attack : release);
        }

        // spatial smoothing so neighbouring bars flow into each other (index 0 and Half are mirrored: no seam at the ends)
        for (int d = 0; d <= Half; d++)
        {
            double prev = _bands[Math.Max(0, d - 1)];
            double next = _bands[Math.Min(Half, d + 1)];
            _shaped[d + 1] = prev * 0.25 + _bands[d] * 0.5 + next * 0.25;
        }
        _shaped[0] = _shaped[2];             // mirror padding for the curve below
        _shaped[Half + 2] = _shaped[Half];

        var brush = Tint ?? TryFindResource("AccentBrush") as Brush ?? Brushes.White;
        var center = new Point(ActualWidth / 2, ActualHeight / 2);

        if (_style == 1) DrawBars(dc, brush, center, intensity);
        else DrawRing(dc, brush, center, intensity);
    }

    private double Shaped(int d) => _shaped[Math.Clamp(d, 0, Half) + 1];

    private void DrawBars(DrawingContext dc, Brush brush, Point c, double intensity)
    {
        var pen = new Pen(brush, 4.2) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
        if (pen.CanFreeze) pen.Freeze();

        for (int i = 0; i < BarCount; i++)
        {
            int d = i <= Half ? i : BarCount - i;                  // mirrored: bar 0 is the bass at the bottom
            double angle = Math.PI / 2 + 2 * Math.PI * i / BarCount;
            double len = 3 + MaxBar * 1.5 * intensity * Shaped(d) * (0.25 + 0.75 * _level);
            len = Math.Min(len, MaxBar * 1.6);

            double cos = Math.Cos(angle), sin = Math.Sin(angle);
            dc.DrawLine(pen,
                new Point(c.X + cos * InnerRadius, c.Y + sin * InnerRadius),
                new Point(c.X + cos * (InnerRadius + len), c.Y + sin * (InnerRadius + len)));
        }
    }

    /// <summary>Catmull-Rom curve through four neighbouring values: a smooth line instead of straight segments.</summary>
    private static double Cubic(double p0, double p1, double p2, double p3, double t) =>
        p1 + 0.5 * t * (p2 - p0 + t * (2 * p0 - 5 * p1 + 4 * p2 - p3 + t * (3 * (p1 - p2) + p3 - p0)));

    private void DrawRing(DrawingContext dc, Brush brush, Point c, double intensity)
    {
        const int points = 240;
        double baseR = InnerRadius + 8;

        var geo = new StreamGeometry();
        using (var ctx = geo.Open())
        {
            for (int i = 0; i < points; i++)
            {
                double u = (double)i / points;                     // 0..1 round the ring
                double angle = Math.PI / 2 + 2 * Math.PI * u;      // starts at the bottom (bass)
                double m = (u <= 0.5 ? u * 2 : (1 - u) * 2) * Half;
                int lo = (int)Math.Floor(m);
                double f = m - lo;
                double a = Cubic(Shaped(lo - 1), Shaped(lo), Shaped(lo + 1), Shaped(lo + 2), f);

                double r = baseR + Math.Clamp(a * MaxBar * 1.25 * intensity * (0.25 + 0.75 * _level), 0, MaxBar * 1.45);
                var pt = new Point(c.X + Math.Cos(angle) * r, c.Y + Math.Sin(angle) * r);
                if (i == 0) ctx.BeginFigure(pt, false, true); else ctx.LineTo(pt, true, true);
            }
        }
        geo.Freeze();

        var thick = new Pen(brush, 3.0) { LineJoin = PenLineJoin.Round };
        var glow = new Pen(brush, 9) { LineJoin = PenLineJoin.Round };
        var thin = new Pen(brush, 1.2) { LineJoin = PenLineJoin.Round };

        dc.PushOpacity(0.25);
        dc.DrawEllipse(null, thin, c, baseR + MaxBar * 1.5, baseR + MaxBar * 1.5);
        dc.Pop();
        dc.PushOpacity(0.16);
        dc.DrawGeometry(null, glow, geo);
        dc.Pop();
        dc.DrawGeometry(null, thick, geo);
    }
}

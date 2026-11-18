using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;

namespace EchoDeck;

/// <summary>The most present vivid colours of a cover picture (what the animated background is made of).</summary>
internal static class CoverPalette
{
    /// <summary>
    /// Up to four colours of the cover itself, the most present first: black if the cover is black, grey if it is grey, and any colour
    /// that takes up a real part of it. Colourful pixels count a little more than dull ones, so a small bright accent on a dark cover
    /// still makes the list (after the dark that dominates it). Never empty.
    /// </summary>
    internal static List<Color> From(BitmapSource source)
    {
        var found = new List<Color>();
        try
        {
            double scale = 40.0 / Math.Max(source.PixelWidth, source.PixelHeight);
            var small = new TransformedBitmap(source, new ScaleTransform(scale, scale));
            var bgra = new FormatConvertedBitmap(small, PixelFormats.Bgra32, null, 0);
            int w = bgra.PixelWidth, h = bgra.PixelHeight, stride = w * 4;
            var px = new byte[stride * h];
            bgra.CopyPixels(px, stride, 0);

            // every pixel goes into one of 512 colour boxes (8 steps per channel), black and white included
            const int bins = 512;
            var weight = new double[bins];
            var sr = new double[bins];
            var sg = new double[bins];
            var sb = new double[bins];

            for (int i = 0; i + 3 < px.Length; i += 4)
            {
                double b = px[i], g = px[i + 1], r = px[i + 2], a = px[i + 3];
                if (a < 40) continue;
                ToHsv(r, g, b, out _, out double sat, out _);

                double wgt = 1 + 2 * sat;
                int bin = ((int)r >> 5) * 64 + ((int)g >> 5) * 8 + ((int)b >> 5);
                weight[bin] += wgt;
                sr[bin] += r * wgt;
                sg[bin] += g * wgt;
                sb[bin] += b * wgt;
            }

            foreach (int bin in Enumerable.Range(0, bins).OrderByDescending(x => weight[x]))
            {
                if (weight[bin] <= 0) break;
                var colour = Color.FromRgb((byte)Math.Round(sr[bin] / weight[bin]), (byte)Math.Round(sg[bin] / weight[bin]), (byte)Math.Round(sb[bin] / weight[bin]));

                // colours that are nearly the same add nothing
                if (found.All(c => Distance(c, colour) > 60)) found.Add(colour);
                if (found.Count == 4) break;
            }
        }
        catch
        {
            // unreadable picture: the accent-coloured default is used instead
        }

        if (found.Count == 0) found.Add(Color.FromRgb(24, 24, 28));

        // fewer than four (a cover of one colour): the same colour, a little lighter or darker, never an invented hue
        int original = found.Count;
        for (int i = 0; original > 0 && found.Count < 4; i++)
        {
            var c = found[i % original];
            found.Add(Mix(c, i % 2 == 0 ? Colors.White : Colors.Black, 0.10));
        }
        return found;
    }

    private static Color Mix(Color a, Color b, double t) => Color.FromRgb(
        (byte)Math.Round(a.R + (b.R - a.R) * t), (byte)Math.Round(a.G + (b.G - a.G) * t), (byte)Math.Round(a.B + (b.B - a.B) * t));

    /// <summary>
    /// The cover as a tiny, already blurred picture (about 48 px, two box blurs). Shown stretched over the window it looks like a
    /// heavily blurred cover, and costs nothing per frame: a real blur effect on a big picture is re-run by the graphics card on
    /// every frame while anything moves above it, and that is what made the animation lag.
    /// </summary>
    internal static BitmapSource? Soft(BitmapSource source)
    {
        try
        {
            double scale = 48.0 / Math.Max(source.PixelWidth, source.PixelHeight);
            var small = new TransformedBitmap(source, new ScaleTransform(scale, scale));
            var bgra = new FormatConvertedBitmap(small, PixelFormats.Bgra32, null, 0);
            int w = bgra.PixelWidth, h = bgra.PixelHeight, stride = w * 4;
            var px = new byte[stride * h];
            bgra.CopyPixels(px, stride, 0);

            var tmp = new byte[px.Length];
            for (int pass = 0; pass < 2; pass++)
            {
                BoxBlur(px, tmp, w, h, horizontal: true);
                BoxBlur(tmp, px, w, h, horizontal: false);
            }

            var result = BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgra32, null, px, stride);
            result.Freeze();
            return result;
        }
        catch
        {
            return null;
        }
    }

    private static void BoxBlur(byte[] src, byte[] dst, int w, int h, bool horizontal)
    {
        const int radius = 2;
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                int r = 0, g = 0, b = 0, a = 0, n = 0;
                for (int k = -radius; k <= radius; k++)
                {
                    int xx = horizontal ? Math.Clamp(x + k, 0, w - 1) : x;
                    int yy = horizontal ? y : Math.Clamp(y + k, 0, h - 1);
                    int i = (yy * w + xx) * 4;
                    b += src[i]; g += src[i + 1]; r += src[i + 2]; a += src[i + 3];
                    n++;
                }
                int o = (y * w + x) * 4;
                dst[o] = (byte)(b / n); dst[o + 1] = (byte)(g / n); dst[o + 2] = (byte)(r / n); dst[o + 3] = (byte)(a / n);
            }
        }
    }

    /// <summary>Four colours around an accent colour (used while a song has no cover).</summary>
    internal static List<Color> Around(Color accent)
    {
        ToHsv(accent.R, accent.G, accent.B, out double hue, out double sat, out double val);
        return new List<Color>
        {
            accent,
            FromHsv(hue + 40, sat, val),
            FromHsv(hue - 40, sat, Math.Clamp(val * 0.9, 0.3, 1)),
            FromHsv(hue, sat, Math.Clamp(val * 0.6, 0.25, 1)),
        };
    }

    private static double Distance(Color a, Color b)
    {
        double dr = a.R - b.R, dg = a.G - b.G, db = a.B - b.B;
        return Math.Sqrt(dr * dr + dg * dg + db * db);
    }

    internal static void ToHsv(double r, double g, double b, out double hue, out double sat, out double val)
    {
        r /= 255; g /= 255; b /= 255;
        double max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b)), delta = max - min;
        hue = 0;
        if (delta > 1e-9)
        {
            if (max == r) hue = ((g - b) / delta) % 6;
            else if (max == g) hue = (b - r) / delta + 2;
            else hue = (r - g) / delta + 4;
            hue *= 60;
            if (hue < 0) hue += 360;
        }
        sat = max <= 0 ? 0 : delta / max;
        val = max;
    }

    internal static Color FromHsv(double hue, double sat, double val)
    {
        hue %= 360;
        if (hue < 0) hue += 360;
        sat = Math.Clamp(sat, 0, 1);
        val = Math.Clamp(val, 0, 1);

        double c = val * sat;
        double x = c * (1 - Math.Abs((hue / 60) % 2 - 1));
        double m = val - c;
        double r, g, b;
        switch ((int)(hue / 60) % 6)
        {
            case 0: r = c; g = x; b = 0; break;
            case 1: r = x; g = c; b = 0; break;
            case 2: r = 0; g = c; b = x; break;
            case 3: r = 0; g = x; b = c; break;
            case 4: r = x; g = 0; b = c; break;
            default: r = c; g = 0; b = x; break;
        }
        return Color.FromRgb((byte)Math.Round((r + m) * 255), (byte)Math.Round((g + m) * 255), (byte)Math.Round((b + m) * 255));
    }
}

/// <summary>
/// The background of the music view: a flowing, liquid blend of the cover's own colours (like Apple Music's), under a dark veil that
/// keeps the text readable. Six colour points wander across the window while the picture they colour is warped by slow waves, so the
/// colours swirl into each other instead of sliding around as separate shapes. The blend is calculated at a tiny size (96 x 54 pixels)
/// and stretched smoothly over the window: it costs almost nothing and needs no graphics-card features. Colours change smoothly from
/// one song to the next.
/// </summary>
public sealed class AuroraBackground : Grid
{
    private const int W = 96, H = 54, Points = 6;

    private readonly Rectangle _base = new() { Fill = new SolidColorBrush(Color.FromRgb(16, 18, 30)) };
    private readonly Image _cover = new() { Stretch = Stretch.UniformToFill, Opacity = 0.20, Visibility = Visibility.Collapsed };
    private readonly Image _fluid = new() { Stretch = Stretch.Fill, IsHitTestVisible = false };
    private readonly Rectangle _veil = new() { IsHitTestVisible = false };

    private readonly WriteableBitmap _bitmap = new(W, H, 96, 96, PixelFormats.Bgra32, null);
    private readonly int[] _pixels = new int[W * H];
    private readonly double[] _current = new double[Points * 3];   // the colours shown now (r, g, b per point)
    private readonly double[] _target = new double[Points * 3];   // the colours they are fading to
    private readonly double[] _sx = new double[W];
    private readonly double[] _sy = new double[H];
    private bool _hasColours;
    private bool _dirty = true;
    private double _time;
    private long _lastTicks;

    private readonly System.Windows.Threading.DispatcherTimer _timer = new(System.Windows.Threading.DispatcherPriority.Render)
    { Interval = TimeSpan.FromMilliseconds(33) };

    // how fast each colour point wanders along its own path (different on each axis, so the paths loop and curve)
    private static readonly double[] SpeedX = { 0.21, 0.17, 0.26, 0.19, 0.23, 0.15 };
    private static readonly double[] SpeedY = { 0.18, 0.25, 0.16, 0.22, 0.14, 0.27 };
    private static readonly double[] PhaseX = { 0.0, 1.9, 3.7, 5.1, 2.6, 4.4 };
    private static readonly double[] PhaseY = { 2.2, 0.4, 4.9, 1.3, 3.3, 5.8 };

    public AuroraBackground()
    {
        IsHitTestVisible = false;
        ClipToBounds = true;

        RenderOptions.SetBitmapScalingMode(_cover, BitmapScalingMode.HighQuality);
        RenderOptions.SetBitmapScalingMode(_fluid, BitmapScalingMode.HighQuality);   // smooth when stretched
        _fluid.Source = _bitmap;

        Children.Add(_base);
        Children.Add(_cover);
        Children.Add(_fluid);

        // a dark veil: the lyrics and titles stay readable on any colours
        _veil.Fill = new LinearGradientBrush(Color.FromArgb(0x10, 0, 0, 0), Color.FromArgb(0x44, 0, 0, 0), 90);
        Children.Add(_veil);

        _timer.Tick += (_, _) => Frame();
        IsVisibleChanged += (_, _) => { if (IsVisible) StartClock(); else _timer.Stop(); };
        Loaded += (_, _) => { if (IsVisible) StartClock(); };
        Unloaded += (_, _) => _timer.Stop();
        SizeChanged += (_, _) => _dirty = true;
    }

    private void StartClock()
    {
        _lastTicks = System.Diagnostics.Stopwatch.GetTimestamp();
        _dirty = true;
        _timer.Start();
    }

    /// <summary>New colours (and the blurred cover behind them): everything fades over to them in about a second.</summary>
    public void SetPalette(IReadOnlyList<Color> colours, ImageSource? cover)
    {
        if (colours.Count == 0) return;

        // the cover's own colours, as they are (black stays black, grey stays grey): nothing is made more vivid
        for (int i = 0; i < Points; i++)
        {
            var c = colours[i % colours.Count];
            _target[i * 3] = c.R;
            _target[i * 3 + 1] = c.G;
            _target[i * 3 + 2] = c.B;
        }
        if (!_hasColours)
        {
            Array.Copy(_target, _current, _target.Length);
            _hasColours = true;
        }
        _dirty = true;

        // the deep base colour: the first colour, darkened
        CoverPalette.ToHsv(colours[0].R, colours[0].G, colours[0].B, out double hue, out double sat, out double val);
        var baseColour = CoverPalette.FromHsv(hue, Math.Clamp(sat * 0.95, 0, 1), Math.Clamp(val * 0.55, 0, 0.50));
        if (_base.Fill is SolidColorBrush brush)
            brush.BeginAnimation(SolidColorBrush.ColorProperty,
                new ColorAnimation(baseColour, TimeSpan.FromMilliseconds(1000)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseInOut } });

        var soft = cover is BitmapSource bitmap ? CoverPalette.Soft(bitmap) : null;
        _cover.Source = soft;
        _cover.Visibility = soft != null ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>How strong the dark veil over the colours is: 1 = as in the music panel, less when the colours also sit behind the side bar and bottom bar.</summary>
    public double VeilStrength
    {
        get => _veil.Opacity;
        set => _veil.Opacity = Math.Clamp(value, 0, 1);
    }

    /// <summary>False: the colours stay still (Settings > Interface > Moving background).</summary>
    public bool MotionEnabled { get; set; } = true;

    private void Frame()
    {
        if (!IsVisible || !_hasColours) return;

        long now = System.Diagnostics.Stopwatch.GetTimestamp();
        double dt = Math.Min(0.1, (now - _lastTicks) / (double)System.Diagnostics.Stopwatch.Frequency);
        _lastTicks = now;

        // ease the colours toward the target ones (about a second)
        double k = 1 - Math.Exp(-dt * 3.0);
        double change = 0;
        for (int i = 0; i < _current.Length; i++)
        {
            double d = _target[i] - _current[i];
            _current[i] += d * k;
            change += Math.Abs(d);
        }

        if (MotionEnabled) _time += dt;
        else if (change < 0.5 && !_dirty) return;   // still, and nothing is changing: nothing to draw

        _dirty = false;
        Render();
    }

    private void Render()
    {
        double aspect = ActualHeight > 0 ? Math.Clamp(ActualWidth / ActualHeight, 0.6, 3.0) : 1.78;
        double t = _time;

        // where each colour point is now
        Span<double> cx = stackalloc double[Points];
        Span<double> cy = stackalloc double[Points];
        for (int i = 0; i < Points; i++)
        {
            cx[i] = aspect * (0.5 + 0.46 * Math.Sin(SpeedX[i] * t * 1.6 + PhaseX[i]));
            cy[i] = 0.5 + 0.46 * Math.Sin(SpeedY[i] * t * 1.6 + PhaseY[i]);
        }

        // the slow waves that bend the picture: one value per column and per row, so the swirling costs almost nothing
        for (int x = 0; x < W; x++)
        {
            double u = (x + 0.5) / W * aspect;
            _sx[x] = 0.20 * Math.Sin(2.3 * u - 0.50 * t + 0.7) + 0.10 * Math.Sin(4.4 * u + 0.80 * t);
        }
        for (int y = 0; y < H; y++)
        {
            double v = (y + 0.5) / H;
            _sy[y] = 0.20 * Math.Sin(2.6 * v + 0.55 * t) + 0.10 * Math.Sin(4.7 * v - 0.75 * t + 1.3);
        }

        for (int y = 0; y < H; y++)
        {
            double v = (y + 0.5) / H;
            for (int x = 0; x < W; x++)
            {
                double u = (x + 0.5) / W * aspect;
                double qx = u + _sy[y];   // a column is bent by the row's wave and the other way round
                double qy = v + _sx[x];

                double sw = 0, sr = 0, sg = 0, sb = 0;
                for (int i = 0; i < Points; i++)
                {
                    double dx = qx - cx[i], dy = qy - cy[i];
                    double d2 = dx * dx + dy * dy + 0.012;
                    double w = 1.0 / (d2 * d2);   // the nearer colour points count much more: smooth, liquid blends
                    sw += w;
                    sr += w * _current[i * 3];
                    sg += w * _current[i * 3 + 1];
                    sb += w * _current[i * 3 + 2];
                }
                int r = (int)(sr / sw), g = (int)(sg / sw), b = (int)(sb / sw);
                _pixels[y * W + x] = (255 << 24) | (r << 16) | (g << 8) | b;
            }
        }

        _bitmap.WritePixels(new Int32Rect(0, 0, W, H), _pixels, W * 4, 0);
    }
}

internal static class AnimationRate
{
    /// <summary>The background motion is drawn 30 times a second (not 60): the graphics card does half the work and it still looks smooth.</summary>
    internal static DoubleAnimation AtLowRate(this DoubleAnimation animation)
    {
        Timeline.SetDesiredFrameRate(animation, 30);
        return animation;
    }
}

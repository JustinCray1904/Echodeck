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
/// The background of the music view: the cover blurred into soft colour, with four large glows (in the cover's own colours) that
/// drift and breathe slowly, under a dark veil that keeps the text readable. Colours change smoothly from one song to the next.
/// </summary>
public sealed class AuroraBackground : Grid
{
    private const int BlobCount = 6;

    private readonly Rectangle _base = new() { Fill = new SolidColorBrush(Color.FromRgb(16, 18, 30)) };
    private readonly Image _cover = new() { Stretch = Stretch.UniformToFill, Opacity = 0.38, Visibility = Visibility.Collapsed };
    private readonly Rectangle _veil = new() { IsHitTestVisible = false };

    // a computer that draws everything in software (no graphics card acceleration) gets still colours instead of motion
    private static readonly bool Accelerated = (RenderCapability.Tier >> 16) >= 1;
    private readonly Canvas _canvas = new() { IsHitTestVisible = false };
    private readonly Ellipse[] _blobs = new Ellipse[BlobCount];
    private readonly GradientStop[] _centreStops = new GradientStop[BlobCount];
    private readonly GradientStop[] _midStops = new GradientStop[BlobCount];
    private readonly GradientStop[] _edgeStops = new GradientStop[BlobCount];
    private readonly TranslateTransform[] _moves = new TranslateTransform[BlobCount];
    private readonly ScaleTransform[] _scales = new ScaleTransform[BlobCount];
    private readonly RotateTransform[] _spins = new RotateTransform[BlobCount];
    private bool _moving;

    // six smaller blobs, spread over the window; each one drifts, stretches and turns on its own timing, so together they flow like a liquid
    private static readonly (double X, double Y)[] Anchors = { (0.15, 0.20), (0.82, 0.16), (0.50, 0.52), (0.18, 0.84), (0.84, 0.80), (0.60, 0.10) };
    private static readonly double[] MoveSecondsX = { 7.0, 9.5, 8.0, 10.5, 6.5, 8.8 };
    private static readonly double[] MoveSecondsY = { 9.0, 6.8, 11.0, 7.4, 9.6, 7.9 };
    private static readonly double[] StretchSecondsX = { 4.6, 5.8, 4.1, 6.3, 5.2, 4.8 };
    private static readonly double[] StretchSecondsY = { 6.1, 4.4, 5.5, 4.9, 6.6, 5.0 };
    private static readonly double[] SpinSeconds = { 13, 17, 11, 15, 19, 12 };

    public AuroraBackground()
    {
        IsHitTestVisible = false;
        ClipToBounds = true;

        RenderOptions.SetBitmapScalingMode(_cover, BitmapScalingMode.HighQuality);

        Children.Add(_base);
        Children.Add(_cover);
        Children.Add(_canvas);

        for (int i = 0; i < BlobCount; i++)
        {
            _centreStops[i] = new GradientStop(Color.FromArgb(0xFF, 120, 130, 190), 0.0);
            _edgeStops[i] = new GradientStop(Color.FromArgb(0x00, 90, 100, 160), 1.0);
            _midStops[i] = new GradientStop(Color.FromArgb(0xB4, 90, 100, 160), 0.50);
            var glow = new RadialGradientBrush { Center = new Point(0.5, 0.5), GradientOrigin = new Point(0.5, 0.5), RadiusX = 0.5, RadiusY = 0.5 };
            glow.GradientStops.Add(_centreStops[i]);
            glow.GradientStops.Add(_midStops[i]);
            glow.GradientStops.Add(_edgeStops[i]);

            _moves[i] = new TranslateTransform();
            _scales[i] = new ScaleTransform(1, 1);
            _spins[i] = new RotateTransform(0);
            var group = new TransformGroup();
            group.Children.Add(_scales[i]);
            group.Children.Add(_spins[i]);
            group.Children.Add(_moves[i]);

            _blobs[i] = new Ellipse { Fill = glow, RenderTransformOrigin = new Point(0.5, 0.5), RenderTransform = group, IsHitTestVisible = false };
            _canvas.Children.Add(_blobs[i]);
        }

        // a dark veil: the lyrics and titles stay readable on any colours
        _veil.Fill = new LinearGradientBrush(Color.FromArgb(0x10, 0, 0, 0), Color.FromArgb(0x44, 0, 0, 0), 90);
        Children.Add(_veil);

        SizeChanged += (_, _) => Relayout();
        IsVisibleChanged += (_, _) => { if (IsVisible) StartMotion(); else StopMotion(); };
        Loaded += (_, _) => { Relayout(); if (IsVisible) StartMotion(); };
        Unloaded += (_, _) => StopMotion();
    }

    /// <summary>New colours (and the blurred cover behind them): everything fades over to them in about a second.</summary>
    public void SetPalette(IReadOnlyList<Color> colours, ImageSource? cover)
    {
        if (colours.Count == 0) return;

        var ms = TimeSpan.FromMilliseconds(1000);
        var ease = new CubicEase { EasingMode = EasingMode.EaseInOut };

        for (int i = 0; i < BlobCount; i++)
        {
            // the glows are the cover's own colours, as they are (black stays black, grey stays grey): nothing is made more vivid
            var c = colours[i % colours.Count];
            CoverPalette.ToHsv(c.R, c.G, c.B, out _, out _, out double cv);

            // a glow has a brighter core: the middle of each blob is the colour lightened a little (less for dark colours, so a
            // black cover does not turn grey), fading out to the colour itself
            double lift = 0.30 * (0.35 + 0.65 * cv);
            var core = Color.FromRgb((byte)(c.R + (255 - c.R) * lift), (byte)(c.G + (255 - c.G) * lift), (byte)(c.B + (255 - c.B) * lift));
            _centreStops[i].BeginAnimation(GradientStop.ColorProperty, new ColorAnimation(Color.FromArgb(0xFF, core.R, core.G, core.B), ms) { EasingFunction = ease });
            _midStops[i].BeginAnimation(GradientStop.ColorProperty, new ColorAnimation(Color.FromArgb(0xB8, c.R, c.G, c.B), ms) { EasingFunction = ease });
            _edgeStops[i].BeginAnimation(GradientStop.ColorProperty, new ColorAnimation(Color.FromArgb(0x00, c.R, c.G, c.B), ms) { EasingFunction = ease });
        }

        // the deep base colour: the first colour, darkened
        CoverPalette.ToHsv(colours[0].R, colours[0].G, colours[0].B, out double hue, out double sat, out double val);
        var baseColour = CoverPalette.FromHsv(hue, Math.Clamp(sat * 0.95, 0, 1), Math.Clamp(val * 0.55, 0, 0.50));
        if (_base.Fill is SolidColorBrush brush)
            brush.BeginAnimation(SolidColorBrush.ColorProperty, new ColorAnimation(baseColour, ms) { EasingFunction = ease });

        var soft = cover is BitmapSource bitmap ? CoverPalette.Soft(bitmap) : null;
        _cover.Source = soft;
        _cover.Visibility = soft != null ? Visibility.Visible : Visibility.Collapsed;
    }

    private void Relayout()
    {
        double w = ActualWidth, h = ActualHeight;
        if (w <= 0 || h <= 0) return;

        double size = Math.Max(240, Math.Max(w, h) * 0.46);
        for (int i = 0; i < BlobCount; i++)
        {
            _blobs[i].Width = size;
            _blobs[i].Height = size;
            Canvas.SetLeft(_blobs[i], Anchors[i].X * w - size / 2);
            Canvas.SetTop(_blobs[i], Anchors[i].Y * h - size / 2);
        }
        if (_moving) StartMotion();   // the drift distance follows the size
    }

    /// <summary>How strong the dark veil over the colours is: 1 = as in the music panel, less when the colours also sit behind the side bar and bottom bar.</summary>
    public double VeilStrength
    {
        get => _veil.Opacity;
        set => _veil.Opacity = Math.Clamp(value, 0, 1);
    }

    private void StartMotion()
    {
        double w = ActualWidth, h = ActualHeight;
        if (w <= 0 || h <= 0) return;
        _moving = true;
        if (!Accelerated) return;   // software drawing: still colours

        for (int i = 0; i < BlobCount; i++)
        {
            // wide sweeps across the window (different timing on each axis, so the paths loop and curve instead of going back and forth)
            double dx = w * (i % 2 == 0 ? 0.34 : -0.34), dy = h * (i % 3 == 0 ? 0.36 : -0.36);
            var ease = new SineEase { EasingMode = EasingMode.EaseInOut };

            _moves[i].BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(-dx, dx, TimeSpan.FromSeconds(MoveSecondsX[i]))
                { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever, EasingFunction = ease }.AtLowRate());
            _moves[i].BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(dy, -dy, TimeSpan.FromSeconds(MoveSecondsY[i]))
                { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever, EasingFunction = ease }.AtLowRate());

            // the blob stretches one way while it squashes the other, and turns slowly: this is what makes it look like a liquid
            _scales[i].BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(0.7, 1.55, TimeSpan.FromSeconds(StretchSecondsX[i]))
                { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever, EasingFunction = ease }.AtLowRate());
            _scales[i].BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(1.45, 0.65, TimeSpan.FromSeconds(StretchSecondsY[i]))
                { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever, EasingFunction = ease }.AtLowRate());
            _spins[i].BeginAnimation(RotateTransform.AngleProperty, new DoubleAnimation(i % 2 == 0 ? 0 : 360, i % 2 == 0 ? 360 : 0, TimeSpan.FromSeconds(SpinSeconds[i]))
                { RepeatBehavior = RepeatBehavior.Forever }.AtLowRate());
        }
    }

    private void StopMotion()
    {
        _moving = false;
        for (int i = 0; i < BlobCount; i++)
        {
            _moves[i].BeginAnimation(TranslateTransform.XProperty, null);
            _moves[i].BeginAnimation(TranslateTransform.YProperty, null);
            _scales[i].BeginAnimation(ScaleTransform.ScaleXProperty, null);
            _scales[i].BeginAnimation(ScaleTransform.ScaleYProperty, null);
            _spins[i].BeginAnimation(RotateTransform.AngleProperty, null);
        }
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

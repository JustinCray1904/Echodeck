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
    /// <summary>Up to four colours, the most present first. Never empty.</summary>
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

            const int bins = 12;
            var weight = new double[bins];
            var sr = new double[bins];
            var sg = new double[bins];
            var sb = new double[bins];
            double grey = 0;
            int greyCount = 0;

            for (int i = 0; i + 3 < px.Length; i += 4)
            {
                double b = px[i], g = px[i + 1], r = px[i + 2], a = px[i + 3];
                if (a < 40) continue;
                ToHsv(r, g, b, out double hue, out double sat, out double val);
                grey += val;
                greyCount++;
                if (val < 0.16 || (sat < 0.10 && val > 0.80)) continue;   // black and white say nothing about the colours

                double wgt = 0.04 + sat * val;
                int bin = (int)(hue / 30) % bins;
                weight[bin] += wgt;
                sr[bin] += r * wgt;
                sg[bin] += g * wgt;
                sb[bin] += b * wgt;
            }

            foreach (int bin in Enumerable.Range(0, bins).OrderByDescending(x => weight[x]))
            {
                if (weight[bin] <= 0.05) break;
                double r = sr[bin] / weight[bin], g = sg[bin] / weight[bin], b = sb[bin] / weight[bin];
                ToHsv(r, g, b, out double hue, out double sat, out double val);
                var colour = FromHsv(hue, Math.Clamp(sat * 1.1, 0.30, 1.0), Math.Clamp(val, 0.45, 0.95));

                // colours that are nearly the same add nothing
                if (found.All(c => Distance(c, colour) > 70)) found.Add(colour);
                if (found.Count == 4) break;
            }

            if (found.Count == 0)
            {
                // a black-and-white cover: soft blue-greys of its own brightness
                double v = greyCount > 0 ? Math.Clamp(grey / greyCount, 0.35, 0.8) : 0.5;
                found.Add(FromHsv(220, 0.12, v));
                found.Add(FromHsv(240, 0.18, v * 0.8));
            }
        }
        catch
        {
            // unreadable picture: the accent-coloured default is used instead
        }

        // fewer than four: variations of the ones found
        int original = found.Count;
        for (int i = 0; original > 0 && found.Count < 4; i++)
        {
            ToHsv(found[i % original].R, found[i % original].G, found[i % original].B, out double hue, out double sat, out double val);
            found.Add(FromHsv(hue + (i % 2 == 0 ? 35 : -35), sat, Math.Clamp(val * 0.85, 0.35, 0.95)));
        }
        return found;
    }

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
    private const int BlobCount = 4;

    private readonly Rectangle _base = new() { Fill = new SolidColorBrush(Color.FromRgb(16, 18, 30)) };
    private readonly Image _cover = new() { Stretch = Stretch.UniformToFill, Opacity = 0.55, Visibility = Visibility.Collapsed };
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
    private bool _moving;

    private static readonly (double X, double Y)[] Anchors = { (0.06, 0.10), (0.88, 0.06), (0.10, 0.92), (0.92, 0.86) };
    private static readonly double[] MoveSeconds = { 19, 27, 23, 31 };
    private static readonly double[] BreatheSeconds = { 15, 21, 18, 24 };

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
            _centreStops[i] = new GradientStop(Color.FromArgb(0xF0, 90, 100, 160), 0.0);
            _edgeStops[i] = new GradientStop(Color.FromArgb(0x00, 90, 100, 160), 1.0);
            _midStops[i] = new GradientStop(Color.FromArgb(0xA0, 90, 100, 160), 0.55);
            var glow = new RadialGradientBrush { Center = new Point(0.5, 0.5), GradientOrigin = new Point(0.5, 0.5), RadiusX = 0.5, RadiusY = 0.5 };
            glow.GradientStops.Add(_centreStops[i]);
            glow.GradientStops.Add(_midStops[i]);
            glow.GradientStops.Add(_edgeStops[i]);

            _moves[i] = new TranslateTransform();
            _scales[i] = new ScaleTransform(1, 1);
            var group = new TransformGroup();
            group.Children.Add(_scales[i]);
            group.Children.Add(_moves[i]);

            _blobs[i] = new Ellipse { Fill = glow, RenderTransformOrigin = new Point(0.5, 0.5), RenderTransform = group, IsHitTestVisible = false };
            _canvas.Children.Add(_blobs[i]);
        }

        // a dark veil: the lyrics and titles stay readable on any colours
        _veil.Fill = new LinearGradientBrush(Color.FromArgb(0x26, 0, 0, 0), Color.FromArgb(0x62, 0, 0, 0), 90);
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
            // the glows are the vivid colours: bright and saturated, so they stand out from the darker base
            var c = colours[i % colours.Count];
            CoverPalette.ToHsv(c.R, c.G, c.B, out double ch, out double cs, out double cv);
            c = CoverPalette.FromHsv(ch, Math.Max(cs, 0.60), Math.Max(cv, 0.70));
            _centreStops[i].BeginAnimation(GradientStop.ColorProperty, new ColorAnimation(Color.FromArgb(0xF0, c.R, c.G, c.B), ms) { EasingFunction = ease });
            _midStops[i].BeginAnimation(GradientStop.ColorProperty, new ColorAnimation(Color.FromArgb(0xA0, c.R, c.G, c.B), ms) { EasingFunction = ease });
            _edgeStops[i].BeginAnimation(GradientStop.ColorProperty, new ColorAnimation(Color.FromArgb(0x00, c.R, c.G, c.B), ms) { EasingFunction = ease });
        }

        // the deep base colour: the first colour, darkened
        CoverPalette.ToHsv(colours[0].R, colours[0].G, colours[0].B, out double hue, out double sat, out double val);
        var baseColour = CoverPalette.FromHsv(hue, Math.Clamp(sat * 0.95, 0, 1), Math.Clamp(val * 0.42, 0.14, 0.45));
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

        double size = Math.Max(360, Math.Max(w, h) * 0.95);
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
            double dx = w * (i % 2 == 0 ? 0.30 : -0.30), dy = h * (i < 2 ? 0.28 : -0.28);
            var ease = new SineEase { EasingMode = EasingMode.EaseInOut };

            _moves[i].BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(-dx, dx, TimeSpan.FromSeconds(MoveSeconds[i]))
                { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever, EasingFunction = ease }.AtLowRate());
            _moves[i].BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(dy, -dy, TimeSpan.FromSeconds(MoveSeconds[(i + 2) % BlobCount] * 0.9))
                { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever, EasingFunction = ease }.AtLowRate());

            var breathe = new DoubleAnimation(0.8, 1.3, TimeSpan.FromSeconds(BreatheSeconds[i]))
                { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever, EasingFunction = ease }.AtLowRate();
            _scales[i].BeginAnimation(ScaleTransform.ScaleXProperty, breathe);
            _scales[i].BeginAnimation(ScaleTransform.ScaleYProperty, breathe);
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
        }
    }
}

internal static class AnimationRate
{
    /// <summary>The slow background drift is drawn 24 times a second: nobody sees the difference, the graphics card does the work half as often.</summary>
    internal static DoubleAnimation AtLowRate(this DoubleAnimation animation)
    {
        Timeline.SetDesiredFrameRate(animation, 24);
        return animation;
    }
}

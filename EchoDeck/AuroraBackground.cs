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
    private readonly Image _cover = new() { Stretch = Stretch.UniformToFill, Opacity = 0.5, Visibility = Visibility.Collapsed, RenderTransformOrigin = new Point(0.5, 0.5) };
    private readonly Canvas _canvas = new() { IsHitTestVisible = false };
    private readonly Ellipse[] _blobs = new Ellipse[BlobCount];
    private readonly GradientStop[] _centreStops = new GradientStop[BlobCount];
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

        _cover.RenderTransform = new ScaleTransform(1.35, 1.35);
        _cover.Effect = new BlurEffect { Radius = 70, KernelType = KernelType.Gaussian };

        Children.Add(_base);
        Children.Add(_cover);
        Children.Add(_canvas);

        for (int i = 0; i < BlobCount; i++)
        {
            _centreStops[i] = new GradientStop(Color.FromArgb(0xC0, 90, 100, 160), 0.0);
            _edgeStops[i] = new GradientStop(Color.FromArgb(0x00, 90, 100, 160), 1.0);
            var glow = new RadialGradientBrush { Center = new Point(0.5, 0.5), GradientOrigin = new Point(0.5, 0.5), RadiusX = 0.5, RadiusY = 0.5 };
            glow.GradientStops.Add(_centreStops[i]);
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
        Children.Add(new Rectangle
        {
            IsHitTestVisible = false,
            Fill = new LinearGradientBrush(Color.FromArgb(0x50, 0, 0, 0), Color.FromArgb(0x8C, 0, 0, 0), 90),
        });

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
            var c = colours[i % colours.Count];
            _centreStops[i].BeginAnimation(GradientStop.ColorProperty, new ColorAnimation(Color.FromArgb(0xC0, c.R, c.G, c.B), ms) { EasingFunction = ease });
            _edgeStops[i].BeginAnimation(GradientStop.ColorProperty, new ColorAnimation(Color.FromArgb(0x00, c.R, c.G, c.B), ms) { EasingFunction = ease });
        }

        // the deep base colour: the first colour, darkened
        CoverPalette.ToHsv(colours[0].R, colours[0].G, colours[0].B, out double hue, out double sat, out double val);
        var baseColour = CoverPalette.FromHsv(hue, Math.Clamp(sat * 0.9, 0, 1), Math.Clamp(val * 0.32, 0.10, 0.35));
        if (_base.Fill is SolidColorBrush brush)
            brush.BeginAnimation(SolidColorBrush.ColorProperty, new ColorAnimation(baseColour, ms) { EasingFunction = ease });

        _cover.Source = cover;
        _cover.Visibility = cover != null ? Visibility.Visible : Visibility.Collapsed;
    }

    private void Relayout()
    {
        double w = ActualWidth, h = ActualHeight;
        if (w <= 0 || h <= 0) return;

        double size = Math.Max(320, Math.Max(w, h) * 0.85);
        for (int i = 0; i < BlobCount; i++)
        {
            _blobs[i].Width = size;
            _blobs[i].Height = size;
            Canvas.SetLeft(_blobs[i], Anchors[i].X * w - size / 2);
            Canvas.SetTop(_blobs[i], Anchors[i].Y * h - size / 2);
        }
        if (_moving) StartMotion();   // the drift distance follows the size
    }

    private void StartMotion()
    {
        double w = ActualWidth, h = ActualHeight;
        if (w <= 0 || h <= 0) return;
        _moving = true;

        for (int i = 0; i < BlobCount; i++)
        {
            double dx = w * (i % 2 == 0 ? 0.22 : -0.22), dy = h * (i < 2 ? 0.20 : -0.20);
            var ease = new SineEase { EasingMode = EasingMode.EaseInOut };

            _moves[i].BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(-dx, dx, TimeSpan.FromSeconds(MoveSeconds[i]))
                { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever, EasingFunction = ease });
            _moves[i].BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(dy, -dy, TimeSpan.FromSeconds(MoveSeconds[(i + 2) % BlobCount] * 0.9))
                { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever, EasingFunction = ease });

            var breathe = new DoubleAnimation(0.85, 1.22, TimeSpan.FromSeconds(BreatheSeconds[i]))
                { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever, EasingFunction = ease };
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

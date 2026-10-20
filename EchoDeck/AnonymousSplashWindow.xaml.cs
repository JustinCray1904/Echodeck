using System.Diagnostics;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace EchoDeck;

/// <summary>
/// The "going dark" splash: a black screen closes over the player like an iris, echo rings ripple out from a mask
/// glyph, then the lights come back up in Anonymous Mode. It is a separate top-level window because the video
/// surface is a native child window that WPF content cannot draw over. Frames are driven manually (one step per
/// render) so the iris clip is always rebuilt from plain numbers.
/// </summary>
public partial class AnonymousSplashWindow : Window
{
    private readonly double _w, _h, _maxRadius;
    private readonly bool _entering;

    public AnonymousSplashWindow(Rect bounds, bool entering)
    {
        InitializeComponent();
        _entering = entering;

        Left = bounds.Left;
        Top = bounds.Top;
        Width = Math.Max(200, bounds.Width);
        Height = Math.Max(200, bounds.Height);
        _w = Width;
        _h = Height;
        _maxRadius = Math.Sqrt(_w * _w + _h * _h) / 2 + 24;

        SetHole(_maxRadius); // starts fully "open" (nothing covered)
        SourceInitialized += (_, _) => NativeMethods.MakeNonActivating(new WindowInteropHelper(this).Handle);
    }

    private void SetHole(double radius)
    {
        radius = Math.Max(0, radius);
        var full = new RectangleGeometry(new Rect(0, 0, _w, _h));
        var hole = new EllipseGeometry(new Point(_w / 2, _h / 2), radius, radius);
        var clip = new CombinedGeometry(GeometryCombineMode.Exclude, full, hole);
        clip.Freeze();
        Cover.Clip = clip;
    }

    /// <summary>Instantly fully black (used for the instant-exit cover).</summary>
    public void SetBlack() => SetHole(0);

    private static double EaseInOut(double t) =>
        t < 0.5 ? 4 * t * t * t : 1 - Math.Pow(-2 * t + 2, 3) / 2;

    private static Task Animate(double milliseconds, Action<double> step)
    {
        var tcs = new TaskCompletionSource();
        var clock = Stopwatch.StartNew();

        void OnRender(object? sender, EventArgs e)
        {
            double t = Math.Min(1.0, clock.Elapsed.TotalMilliseconds / milliseconds);
            try { step(t); }
            catch { t = 1.0; }

            if (t >= 1.0)
            {
                CompositionTarget.Rendering -= OnRender;
                tcs.TrySetResult();
            }
        }

        CompositionTarget.Rendering += OnRender;
        return tcs.Task;
    }

    /// <summary>Iris closes, echo rings ripple, the title appears. Returns while the screen is completely dark.</summary>
    public async Task PlayDarkenAsync()
    {
        // 1) the iris closes
        await Animate(650, t => SetHole(_maxRadius * (1 - EaseInOut(t))));
        SetHole(0);

        // 2) echo rings + title over the black
        await Animate(1000, p =>
        {
            Emblem.Opacity = Math.Min(1.0, p / 0.3);
            Pulse(Ring1, Ring1Scale, p, 0);
            Pulse(Ring2, Ring2Scale, p, 1);
            Pulse(Ring3, Ring3Scale, p, 2);
        });

        // 3) hold for a beat so it registers
        await Animate(350, _ => { });
    }

    private static void Pulse(UIElement ring, ScaleTransform scale, double p, int index)
    {
        double local = Math.Clamp((p - index * 0.18) / 0.64, 0, 1);
        double s = 0.3 + 1.25 * local;
        scale.ScaleX = s;
        scale.ScaleY = s;
        ring.Opacity = local <= 0 ? 0 : 0.75 * (1 - local);
    }

    /// <summary>The lights come back up: emblem fades, the iris opens onto the (already swapped) player.</summary>
    public async Task PlayRevealAsync()
    {
        if (_entering)
            await Animate(260, t => Emblem.Opacity = 1 - t);

        await Animate(_entering ? 700 : 520, t => SetHole(_maxRadius * EaseInOut(t)));
        SetHole(_maxRadius);
    }
}

using System.Diagnostics;
using System.Windows;
using System.Windows.Media;

namespace EchoDeck;

/// <summary>
/// Small frame-driven tween helper so panels open and close smoothly instead of snapping.
/// One running tween per key: starting a new one for the same element replaces the old one.
/// </summary>
internal static class UiAnim
{
    private static readonly Dictionary<object, Action> Running = new();

    /// <summary>Eases a value from <paramref name="from"/> to <paramref name="to"/> (cubic ease-out), calling <paramref name="step"/> each frame.</summary>
    public static void Tween(object key, double from, double to, int ms, Action<double> step, Action? done = null)
    {
        Cancel(key);
        if (ms <= 0 || Math.Abs(from - to) < 0.0001)
        {
            step(to);
            done?.Invoke();
            return;
        }

        var clock = Stopwatch.StartNew();
        EventHandler? handler = null;

        void Stop()
        {
            CompositionTarget.Rendering -= handler!;
            Running.Remove(key);
        }

        handler = (_, _) =>
        {
            double t = Math.Min(1.0, clock.Elapsed.TotalMilliseconds / ms);
            double eased = 1.0 - Math.Pow(1.0 - t, 3);
            step(from + (to - from) * eased);
            if (t >= 1.0)
            {
                Stop();
                done?.Invoke();
            }
        };

        Running[key] = () => CompositionTarget.Rendering -= handler!;
        CompositionTarget.Rendering += handler;
    }

    /// <summary>Stops every running tween and detaches their frame handlers (used when the window closes).</summary>
    public static void CancelAll()
    {
        foreach (var stop in Running.Values.ToList())
        {
            try { stop(); } catch { /* already detached */ }
        }
        Running.Clear();
    }

    public static void Cancel(object key)
    {
        if (Running.Remove(key, out var stop)) stop();
    }

    /// <summary>Fades an element in (and makes it visible).</summary>
    public static void FadeIn(UIElement el, int ms = 190)
    {
        if (el.Visibility == Visibility.Visible && el.Opacity >= 0.999 && !Running.ContainsKey(el)) return;
        double start = el.Visibility == Visibility.Visible ? el.Opacity : 0;
        el.Opacity = start;
        el.Visibility = Visibility.Visible;
        Tween(el, start, 1, ms, v => el.Opacity = v, () => el.Opacity = 1);
    }

    /// <summary>Fades an element out and then collapses it (its opacity is reset for the next time).</summary>
    public static void FadeOut(UIElement el, int ms = 160, Action? done = null)
    {
        if (el.Visibility != Visibility.Visible) { done?.Invoke(); return; }
        Tween(el, el.Opacity, 0, ms, v => el.Opacity = v, () =>
        {
            el.Visibility = Visibility.Collapsed;
            el.Opacity = 1;
            done?.Invoke();
        });
    }

    /// <summary>True while a tween is running for this element (used to ignore key-mashing instead of stacking transitions).</summary>
    public static bool IsRunning(object key) => Running.ContainsKey(key);

    private static Thickness Scale(Thickness t, double f) => new(t.Left * f, t.Top * f, t.Right * f, t.Bottom * f);

    private static void After(int ms, Action? action)
    {
        if (action == null) return;
        var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(ms) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            action();
        };
        timer.Start();
    }

    /// <summary>
    /// Opens or closes a panel that sits beside the video.
    /// <para>The video is a native window. A layout that changes while it is being resized can flash white, squeeze, or show a
    /// black strip, so <paramref name="beforeLayout"/> / <paramref name="afterLayout"/> let the caller cover the video with a
    /// curtain for exactly the time the layout is changing.</para>
    /// <para>With <paramref name="smooth"/> the panel's width is animated, so the video area slides. Without it the layout
    /// changes once and only the panel fades and slides.</para>
    /// </summary>
    public static void SlidePanel(FrameworkElement panel, bool show, double openWidth, Thickness openMargin,
        bool smooth = false, Action? beforeLayout = null, Action? afterLayout = null, int ms = 210, double slideFrom = -26)
    {
        if (panel.RenderTransform is not TranslateTransform move)
        {
            move = new TranslateTransform();
            panel.RenderTransform = move;
        }

        bool visible = panel.Visibility == Visibility.Visible;

        if (show)
        {
            if (visible && panel.Opacity >= 0.999 && !Running.ContainsKey(panel)) return;

            beforeLayout?.Invoke();
            double startOpacity = visible ? panel.Opacity : 0;

            if (smooth)
            {
                double startW = visible && !double.IsNaN(panel.Width) ? Math.Min(panel.Width, openWidth) : 0;
                double f0 = openWidth <= 0 ? 1 : startW / openWidth;
                panel.Width = startW;
                panel.Margin = Scale(openMargin, f0);
                panel.Opacity = Math.Min(1, f0 * 1.5);
                move.X = 0;
                panel.Visibility = Visibility.Visible;

                Tween(panel, startW, openWidth, 260, w =>
                {
                    double f = openWidth <= 0 ? 1 : w / openWidth;
                    panel.Width = w;
                    panel.Margin = Scale(openMargin, f);
                    panel.Opacity = Math.Min(1, f * 1.5);
                }, () =>
                {
                    panel.Width = openWidth;
                    panel.Margin = openMargin;
                    panel.Opacity = 1;
                    afterLayout?.Invoke();
                });
                return;
            }

            panel.Width = openWidth;
            panel.Margin = openMargin;
            panel.Opacity = startOpacity;
            move.X = slideFrom * (1 - startOpacity);
            panel.Visibility = Visibility.Visible;

            Tween(panel, startOpacity, 1, ms, v =>
            {
                panel.Opacity = v;
                move.X = slideFrom * (1 - v);
            }, () =>
            {
                panel.Opacity = 1;
                move.X = 0;
            });
            After(110, afterLayout);
            return;
        }

        if (!visible) return;

        if (smooth)
        {
            beforeLayout?.Invoke();
            double startW = double.IsNaN(panel.Width) ? panel.ActualWidth : panel.Width;
            Tween(panel, startW, 0, 230, w =>
            {
                double f = openWidth <= 0 ? 0 : Math.Clamp(w / openWidth, 0, 1);
                panel.Width = w;
                panel.Margin = Scale(openMargin, f);
                panel.Opacity = Math.Min(1, f * 1.5);
            }, () =>
            {
                panel.Visibility = Visibility.Collapsed;
                panel.Width = openWidth;
                panel.Margin = openMargin;
                panel.Opacity = 1;
                afterLayout?.Invoke();
            });
            return;
        }

        Tween(panel, panel.Opacity, 0, (int)(ms * 0.75), v =>
        {
            panel.Opacity = v;
            move.X = slideFrom * (1 - v);
        }, () =>
        {
            // the one and only layout change: cover the video, give the space back in a single step, then uncover
            beforeLayout?.Invoke();
            panel.Visibility = Visibility.Collapsed;
            panel.Opacity = 1;
            move.X = 0;
            After(110, afterLayout);
        });
    }
}

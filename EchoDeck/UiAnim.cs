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

    /// <summary>
    /// Opens or closes a panel that sits beside the video. The video lives in a separate native window, which cannot follow a
    /// layout that changes on every frame (it flickers, loses its rounded corners and shows a black strip). So the layout
    /// changes ONCE, at the start of opening / end of closing, and only the panel itself fades and slides.
    /// </summary>
    /// <param name="slideFrom">How far the panel travels while fading (negative = from the left).</param>
    public static void SlidePanel(FrameworkElement panel, bool show, double openWidth, Thickness openMargin, int ms = 210, double slideFrom = -26)
    {
        if (panel.RenderTransform is not TranslateTransform move)
        {
            move = new TranslateTransform();
            panel.RenderTransform = move;
        }

        if (show)
        {
            if (panel.Visibility == Visibility.Visible && panel.Opacity >= 0.999 && !Running.ContainsKey(panel)) return;

            double start = panel.Visibility == Visibility.Visible ? panel.Opacity : 0;
            panel.Width = openWidth;
            panel.Margin = openMargin;
            panel.Opacity = start;
            move.X = slideFrom * (1 - start);
            panel.Visibility = Visibility.Visible;

            Tween(panel, start, 1, ms, v =>
            {
                panel.Opacity = v;
                move.X = slideFrom * (1 - v);
            }, () =>
            {
                panel.Opacity = 1;
                move.X = 0;
            });
        }
        else
        {
            if (panel.Visibility != Visibility.Visible) return;

            Tween(panel, panel.Opacity, 0, (int)(ms * 0.75), v =>
            {
                panel.Opacity = v;
                move.X = slideFrom * (1 - v);
            }, () =>
            {
                // the one and only layout change: the panel's space is given back in a single step
                panel.Visibility = Visibility.Collapsed;
                panel.Opacity = 1;
                move.X = 0;
            });
        }
    }
}

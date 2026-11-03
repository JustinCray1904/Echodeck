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

    /// <summary>Opens or closes a fixed-width panel by sliding its width (and side margin) with a fade.</summary>
    public static void SlidePanel(FrameworkElement panel, bool show, double openWidth, Thickness openMargin, int ms = 230)
    {
        if (show)
        {
            if (panel.Visibility == Visibility.Visible && Math.Abs(panel.Width - openWidth) < 0.5 && !Running.ContainsKey(panel)) return;
            double startW = panel.Visibility == Visibility.Visible && !double.IsNaN(panel.Width) ? panel.Width : 0;
            panel.Width = startW;
            panel.Opacity = startW <= 0 ? 0 : panel.Opacity;
            panel.Visibility = Visibility.Visible;
            Tween(panel, startW, openWidth, ms, w =>
            {
                double f = openWidth <= 0 ? 1 : w / openWidth;
                panel.Width = w;
                panel.Margin = new Thickness(openMargin.Left * f, openMargin.Top * f, openMargin.Right * f, openMargin.Bottom * f);
                panel.Opacity = Math.Min(1, f * 1.4);
            }, () =>
            {
                panel.Width = openWidth;
                panel.Margin = openMargin;
                panel.Opacity = 1;
            });
        }
        else
        {
            if (panel.Visibility != Visibility.Visible) return;
            double startW = double.IsNaN(panel.Width) ? panel.ActualWidth : panel.Width;
            Tween(panel, startW, 0, ms, w =>
            {
                double f = openWidth <= 0 ? 0 : Math.Clamp(w / openWidth, 0, 1);
                panel.Width = w;
                panel.Margin = new Thickness(openMargin.Left * f, openMargin.Top * f, openMargin.Right * f, openMargin.Bottom * f);
                panel.Opacity = Math.Min(1, f * 1.4);
            }, () =>
            {
                panel.Visibility = Visibility.Collapsed;
                panel.Width = openWidth;
                panel.Margin = openMargin;
                panel.Opacity = 1;
            });
        }
    }
}

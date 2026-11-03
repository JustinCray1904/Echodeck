using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace EchoDeck;

/// <summary>Eased mouse-wheel scrolling for lists (a short glide instead of jumping a few lines at a time).</summary>
internal static class SmoothScroll
{
    /// <summary>
    /// Wires eased wheel scrolling onto the list. The returned action glides the list to an absolute offset with the same
    /// easing (used by the lyrics view to follow the song), and shares the wheel's target so the two never fight.
    /// </summary>
    public static Action<double> Attach(ListBox list, double ease = 0.22)
    {
        ScrollViewer? viewer = null;
        double target = 0;
        bool running = false;

        void Step(object? sender, EventArgs e)
        {
            if (viewer == null) { CompositionTarget.Rendering -= Step; running = false; return; }

            double current = viewer.VerticalOffset;
            double diff = target - current;
            if (Math.Abs(diff) < 0.6)
            {
                viewer.ScrollToVerticalOffset(target);
                CompositionTarget.Rendering -= Step;
                running = false;
                return;
            }
            viewer.ScrollToVerticalOffset(current + diff * ease);
        }

        list.PreviewMouseWheel += (_, e) =>
        {
            viewer ??= Find(list);
            if (viewer == null || viewer.ScrollableHeight <= 0) return;

            e.Handled = true;
            if (!running) target = viewer.VerticalOffset;
            target = Math.Clamp(target - e.Delta * 0.8, 0, viewer.ScrollableHeight);

            if (!running)
            {
                running = true;
                CompositionTarget.Rendering += Step;
            }
        };

        return offset =>
        {
            viewer ??= Find(list);
            if (viewer == null) return;
            target = Math.Clamp(offset, 0, Math.Max(0, viewer.ScrollableHeight));
            if (!running)
            {
                running = true;
                CompositionTarget.Rendering += Step;
            }
        };
    }

    private static ScrollViewer? Find(DependencyObject root)
    {
        int count = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is ScrollViewer sv) return sv;
            if (Find(child) is { } found) return found;
        }
        return null;
    }
}

using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace EchoDeck;

/// <summary>Eased mouse-wheel scrolling for lists (a short glide instead of jumping a few lines at a time).</summary>
internal static class SmoothScroll
{
    public static void Attach(ListBox list)
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
            viewer.ScrollToVerticalOffset(current + diff * 0.22);
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

using System.Windows;
using System.Windows.Input;

namespace EchoDeck;

internal static class WindowHelper
{
    /// <summary>
    /// Wires up a window that uses the "EchoWindow" style: the close button works and the title strip drags the window.
    /// </summary>
    public static void AttachChrome(Window window, double dragHeight = 46)
    {
        window.CommandBindings.Add(new CommandBinding(SystemCommands.CloseWindowCommand, (_, _) => window.Close()));

        window.MouseLeftButtonDown += (_, e) =>
        {
            if (e.GetPosition(window).Y <= dragHeight && e.ButtonState == MouseButtonState.Pressed)
            {
                try { window.DragMove(); } catch (InvalidOperationException) { /* released mid-drag */ }
            }
        };
    }

    /// <summary>Window bounds in device-independent units (screen coordinates), correct even when maximized.</summary>
    public static Rect GetScreenBoundsDip(Window window)
    {
        var source = PresentationSource.FromVisual(window);
        if (source?.CompositionTarget == null || window.Content is not FrameworkElement root)
            return new Rect(window.Left, window.Top, window.ActualWidth, window.ActualHeight);

        var topLeftPx = root.PointToScreen(new Point(0, 0));
        var bottomRightPx = root.PointToScreen(new Point(root.ActualWidth, root.ActualHeight));
        var tl = source.CompositionTarget.TransformFromDevice.Transform(topLeftPx);
        var br = source.CompositionTarget.TransformFromDevice.Transform(bottomRightPx);
        return new Rect(tl, br);
    }
}

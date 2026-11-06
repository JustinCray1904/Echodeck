using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

namespace EchoDeck;

// The small player shown while browsing: drag it left or right, it snaps to a side and remembers which.
// Docked left it sits inside the side bar's footprint; docked right it sits in the bottom-right corner.
public partial class MainWindow
{
    private const double MiniCardWidth = 220;

    private bool _miniDragging;
    private Point _miniDragStart;
    private double _miniDragStartX;

    private FrameworkElement? MiniHost => MiniPlayer.Parent as FrameworkElement;

    private void InitMini()
    {
        MiniPlayer.Width = MiniCardWidth;
        MiniPlayer.MouseLeftButtonDown += Mini_MouseDown;
        MiniPlayer.MouseMove += Mini_MouseMove;
        MiniPlayer.MouseLeftButtonUp += Mini_MouseUp;
        MiniPlayer.LostMouseCapture += (_, _) => _miniDragging = false;

        // no context menu on the small player
        MiniPlayer.MouseRightButtonDown += (_, e) => e.Handled = true;
        MiniPlayer.MouseRightButtonUp += (_, e) => e.Handled = true;

        if (MiniHost is { } host) host.SizeChanged += (_, _) => PositionMini(animate: false);
        Rail.SizeChanged += (_, _) => PositionMini(animate: false);
        Rail.IsVisibleChanged += (_, _) => PositionMini(animate: false);
    }

    /// <summary>The x range (in the mini player's parent) between the two docks.</summary>
    private (double Left, double Right) MiniRange()
    {
        var host = MiniHost;
        if (host == null || host.ActualWidth <= 0) return (6, 6);

        double right = Math.Max(6, host.ActualWidth - MiniCardWidth - 6);
        double left = 6;
        try
        {
            // docked left = inside the side bar (its nav items start 8 px in)
            if (_libraryVisible && Rail.Visibility == Visibility.Visible && Rail.IsLoaded)
                left = Rail.TranslatePoint(new Point(8, 0), host).X;
        }
        catch { /* not in the same tree yet */ }

        return (Math.Min(left, right), right);
    }

    private double MiniDockX(string dock)
    {
        var (left, right) = MiniRange();
        return dock == "left" ? left : right;
    }

    /// <summary>Puts the small player at its remembered side (optionally gliding there).</summary>
    private void PositionMini(bool animate)
    {
        if (MiniPlayer.Visibility != Visibility.Visible || _miniDragging) return;

        double target = MiniDockX(_cfg.MiniDock);
        double current = MiniPlayer.Margin.Left;
        if (!animate || Math.Abs(current - target) < 1) { SetMiniX(target); return; }
        UiAnim.Tween(MiniPlayer, current, target, 280, SetMiniX, () => SetMiniX(target));
    }

    private void SetMiniX(double x)
    {
        MiniPlayer.Margin = new Thickness(x, 0, 0, 6);
        MiniPlayer.UpdateLayout();
        PositionMiniVideo();
    }

    /// <summary>The small video is a native window that cannot live inside the card, so it is laid over the card's video slot.</summary>
    private void PositionMiniVideo()
    {
        if (PlayerView.Parent is not FrameworkElement host) return;
        if (MiniVideoSpacer.Visibility != Visibility.Visible || !MiniPlayer.IsVisible || MiniVideoSpacer.ActualWidth <= 0) return;

        try
        {
            var origin = MiniVideoSpacer.TranslatePoint(new Point(0, 0), host);
            var slot = LayoutInformation.GetLayoutSlot(PlayerView);
            PlayerView.HorizontalAlignment = HorizontalAlignment.Left;
            PlayerView.VerticalAlignment = VerticalAlignment.Top;
            PlayerView.Width = MiniVideoSpacer.ActualWidth;
            PlayerView.Height = MiniVideoSpacer.ActualHeight;
            PlayerView.Margin = new Thickness(origin.X - slot.X, origin.Y - slot.Y, 0, 0);
        }
        catch { /* layout not ready; the next update fixes it */ }
    }

    // ------------------------------------------------------------------ dragging

    private static bool IsOnButton(DependencyObject? d)
    {
        while (d != null)
        {
            if (d is ButtonBase) return true;
            bool visual = d is Visual || d is System.Windows.Media.Media3D.Visual3D;
            d = visual ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d);
        }
        return false;
    }

    private void Mini_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left || IsOnButton(e.OriginalSource as DependencyObject) || MiniHost == null) return;

        _miniDragging = true;
        _miniDragStart = e.GetPosition(MiniHost);
        _miniDragStartX = MiniPlayer.Margin.Left;
        UiAnim.Cancel(MiniPlayer);
        MiniPlayer.CaptureMouse();
        e.Handled = true;
    }

    private void Mini_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_miniDragging || MiniHost == null) return;

        var (left, right) = MiniRange();
        double x = _miniDragStartX + (e.GetPosition(MiniHost).X - _miniDragStart.X);
        SetMiniX(Math.Clamp(x, left, right));   // sideways only
    }

    private void Mini_MouseUp(object sender, MouseButtonEventArgs e)
    {
        if (!_miniDragging) return;
        _miniDragging = false;
        MiniPlayer.ReleaseMouseCapture();

        // snap to whichever side is closer, and remember it
        var (left, right) = MiniRange();
        string dock = MiniPlayer.Margin.Left < (left + right) / 2 ? "left" : "right";
        _cfg.MiniDock = dock;
        _cfg.Save(notify: false);
        PositionMini(animate: true);
    }

    // ------------------------------------------------------------------ popups above the control bar

    private void InitPopups()
    {
        foreach (var pop in new[] { VolumePopup, QuickPopup })
        {
            pop.PlacementTarget = ControlBar;
            pop.Placement = PlacementMode.Custom;
            pop.HorizontalOffset = 0;
            pop.VerticalOffset = 0;
            pop.PopupAnimation = PopupAnimation.None;   // replaced by the slide + fade below
            pop.CustomPopupPlacementCallback = CenterAboveBar;
            pop.Opened += Popup_Opened;
        }
    }

    /// <summary>Centred above the control bar, like the "continue" card.</summary>
    private static CustomPopupPlacement[] CenterAboveBar(Size popupSize, Size targetSize, Point offset) =>
        new[] { new CustomPopupPlacement(new Point((targetSize.Width - popupSize.Width) / 2, -popupSize.Height - 2), PopupPrimaryAxis.None) };

    private void Popup_Opened(object? sender, EventArgs e)
    {
        if (sender is not Popup pop || pop.Child is not FrameworkElement card) return;

        var move = new TranslateTransform(0, 16);
        card.RenderTransform = move;
        card.Opacity = 0;
        UiAnim.Tween(card, 0, 1, 200, v =>
        {
            card.Opacity = v;
            move.Y = 16 * (1 - v);
        }, () =>
        {
            card.Opacity = 1;
            move.Y = 0;
        });
    }
}

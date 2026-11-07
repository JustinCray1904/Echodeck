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

    private readonly HashSet<Popup> _closingPopups = new();

    private void InitPopups()
    {
        Setup(VolumePopup, VolumeButton, alignRight: false);
        Setup(QuickPopup, ToolsBundle, alignRight: true);

        // clicking anywhere else (or leaving the window) closes them, with the same slide + fade as opening
        PreviewMouseDown += (_, e) => DismissPopups(e.OriginalSource as DependencyObject);
        VideoSurface.PreviewMouseDown += (_, e) => DismissPopups(e.OriginalSource as DependencyObject);
        Deactivated += (_, _) =>
        {
            ClosePopup(VolumePopup);
            ClosePopup(QuickPopup);
        };

        void Setup(Popup pop, FrameworkElement anchor, bool alignRight)
        {
            pop.PlacementTarget = anchor;
            pop.Placement = PlacementMode.Custom;
            pop.HorizontalOffset = 0;
            pop.VerticalOffset = 0;
            pop.StaysOpen = true;                        // closed by us, so the close can be animated
            pop.PopupAnimation = PopupAnimation.None;    // the slide + fade below replaces the built-in fade
            pop.CustomPopupPlacementCallback = (popupSize, targetSize, _) => PlaceAboveBar(anchor, popupSize, targetSize, alignRight);
            pop.Opened += Popup_Opened;
        }
    }

    /// <summary>
    /// Over its own button horizontally, and entirely above the control bar vertically (it never overlaps the bar),
    /// leaving the same gap as the "continue" card.
    /// </summary>
    private CustomPopupPlacement[] PlaceAboveBar(FrameworkElement anchor, Size popupSize, Size targetSize, bool alignRight)
    {
        double clearance = 0;
        try { clearance = anchor.TranslatePoint(new Point(0, 0), ControlBar).Y; }   // how far the button sits below the bar's top edge
        catch { /* not in the same tree yet */ }

        const double shadowMargin = 14;   // the popup's own transparent margin (it holds the drop shadow)
        const double gap = 10;            // visible space between the card and the bar
        double y = -clearance - popupSize.Height + shadowMargin - gap;
        double x = alignRight ? targetSize.Width - popupSize.Width + shadowMargin : (targetSize.Width - popupSize.Width) / 2;
        return new[] { new CustomPopupPlacement(new Point(x, y), PopupPrimaryAxis.None) };
    }

    private void DismissPopups(DependencyObject? source)
    {
        if (VolumePopup.IsOpen && VolumePopup.Child is { } volumeCard && !IsInside(source, volumeCard) && !IsInside(source, VolumeButton))
            ClosePopup(VolumePopup);
        if (QuickPopup.IsOpen && QuickPopup.Child is { } quickCard && !IsInside(source, quickCard) && !IsInside(source, ToolsBundle))
            ClosePopup(QuickPopup);
    }

    private void Popup_Opened(object? sender, EventArgs e)
    {
        if (sender is not Popup pop || pop.Child is not FrameworkElement card) return;

        UiAnim.Cancel(pop);
        _closingPopups.Remove(pop);

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

    /// <summary>Closes a popup with a slide + fade (or at once when <paramref name="animate"/> is false).</summary>
    private void ClosePopup(Popup pop, bool animate = true)
    {
        if (!pop.IsOpen) return;

        if (!animate || pop.Child is not FrameworkElement card)
        {
            UiAnim.Cancel(pop);
            _closingPopups.Remove(pop);
            pop.IsOpen = false;
            return;
        }

        if (!_closingPopups.Add(pop)) return;   // already on its way out

        var move = card.RenderTransform as TranslateTransform ?? new TranslateTransform();
        card.RenderTransform = move;
        UiAnim.Cancel(card);
        UiAnim.Tween(pop, card.Opacity, 0, 150, v =>
        {
            card.Opacity = v;
            move.Y = 12 * (1 - v);
        }, () =>
        {
            pop.IsOpen = false;
            _closingPopups.Remove(pop);
            card.Opacity = 1;
            move.Y = 0;
        });
    }
}

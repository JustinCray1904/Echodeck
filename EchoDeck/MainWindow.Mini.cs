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

        MiniOverlay.MouseEnter += (_, _) => FadeMiniControls(1);
        MiniOverlay.MouseLeave += (_, _) => FadeMiniControls(0);

        if (MiniHost is { } host) host.SizeChanged += (_, _) => PositionMini(animate: false);
        Rail.SizeChanged += (_, _) => PositionMini(animate: false);
        Rail.IsVisibleChanged += (_, _) => PositionMini(animate: false);
        Rail.LayoutUpdated += (_, _) => { if (_libraryVisible && MiniPlayer.Visibility == Visibility.Visible && !UiAnim.IsRunning(MiniPlayer) && Math.Abs(MiniPlayer.Margin.Left - MiniDockX(_cfg.MiniDock)) > 1 && !_miniDragging) QueueMiniReposition(); };
    }

    private bool _miniRepositionQueued;

    /// <summary>
    /// Puts the small player at its dock once the layout has settled. The side bar slides in by margin and width, so a position
    /// measured in the middle of that slide can be off by the distance it still had to travel (the card then sat a little left of
    /// the side bar); this runs again after every transition.
    /// </summary>
    private void QueueMiniReposition()
    {
        if (_miniRepositionQueued) return;
        _miniRepositionQueued = true;
        Dispatcher.BeginInvoke(new Action(() =>
        {
            _miniRepositionQueued = false;
            if (MiniPlayer.Visibility == Visibility.Visible) PositionMini(animate: false);
        }), System.Windows.Threading.DispatcherPriority.Loaded);
    }

    /// <summary>The previous / pause / next and open / close buttons on the small video appear while the pointer is over it.</summary>
    private void FadeMiniControls(double to)
    {
        if (to > 0 && MiniOverlay.Visibility != Visibility.Visible) return;
        UiAnim.Tween(MiniControls, MiniControls.Opacity, to, to > 0 ? 160 : 220, v => MiniControls.Opacity = v, () => MiniControls.Opacity = to);
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
    private readonly Dictionary<Popup, (FrameworkElement Anchor, bool AlignRight)> _popupAnchors = new();

    private void InitPopups()
    {
        Setup(VolumePopup, VolumeButton, alignRight: false);
        Setup(QuickPopup, ToolsBundle, alignRight: true);

        // clicking anywhere else (or leaving the window) closes them, with the same slide + fade as opening
        PreviewMouseDown += (_, e) => DismissPopups(e.OriginalSource as DependencyObject);
        VideoSurface.PreviewMouseDown += (_, e) => DismissPopups(e.OriginalSource as DependencyObject);
        MusicLayer.PreviewMouseDown += (_, e) => DismissPopups(e.OriginalSource as DependencyObject);
        Deactivated += (_, _) =>
        {
            ClosePopup(VolumePopup);
            ClosePopup(QuickPopup);
        };
        ControlBar.SizeChanged += (_, _) =>
        {
            if (VolumePopup.IsOpen) PlacePopup(VolumePopup);
            if (QuickPopup.IsOpen) PlacePopup(QuickPopup);
        };

        void Setup(Popup pop, FrameworkElement anchor, bool alignRight)
        {
            _popupAnchors[pop] = (anchor, alignRight);
            pop.PlacementTarget = ControlBar;            // the whole bar: the popup's bottom edge sits on the bar's top edge
            pop.Placement = PlacementMode.Top;
            pop.HorizontalOffset = 0;
            pop.VerticalOffset = 0;
            pop.StaysOpen = true;                        // closed by us, so the close can be animated
            pop.PopupAnimation = PopupAnimation.None;    // the slide + fade below replaces the built-in fade
            pop.Opened += (_, _) => PlacePopup(pop);
            pop.Opened += Popup_Opened;
        }
    }

    private const double PopupGap = 10;   // visible space between a popup card and the control bar (device-independent px)

    /// <summary>
    /// Sideways: over its own button. Upwards: WPF's own "Top" placement puts the popup directly above the bar, then the real
    /// on-screen position of the card is measured and corrected, so the card can never overlap the bar whatever the DPI.
    /// </summary>
    private void PlacePopup(Popup pop)
    {
        if (!_popupAnchors.TryGetValue(pop, out var info) || pop.Child is not FrameworkElement card) return;

        try
        {
            card.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            double popupWidth = card.DesiredSize.Width;                 // includes the card's own margin (room for the shadow)
            var topLeft = info.Anchor.TranslatePoint(new Point(0, 0), ControlBar);

            double x = info.AlignRight
                ? topLeft.X + info.Anchor.ActualWidth - popupWidth + card.Margin.Right   // card's right edge on the tools' right edge
                : topLeft.X + (info.Anchor.ActualWidth - popupWidth) / 2;                // centred over the speaker
            double maxX = ControlBar.ActualWidth - popupWidth + card.Margin.Right;
            pop.HorizontalOffset = Math.Max(-card.Margin.Left + 4, Math.Min(x, maxX));
            pop.VerticalOffset = 0;
        }
        catch { /* layout not ready: the correction below still runs */ }

        Dispatcher.BeginInvoke(new Action(() => CorrectPopupHeight(pop)), System.Windows.Threading.DispatcherPriority.Loaded);
    }

    private void CorrectPopupHeight(Popup pop)
    {
        if (!pop.IsOpen || pop.Child is not FrameworkElement card || !card.IsVisible || card.ActualHeight <= 0) return;

        try
        {
            double scale = VisualTreeHelper.GetDpi(this).DpiScaleY;
            double slide = (card.RenderTransform as TranslateTransform)?.Y ?? 0;           // the opening animation may still be sliding it
            double cardBottom = card.PointToScreen(new Point(0, card.ActualHeight)).Y - slide * scale;
            double barTop = ControlBar.PointToScreen(new Point(0, 0)).Y;
            double wanted = barTop - PopupGap * scale;

            double deltaPx = wanted - cardBottom;      // negative: too low (overlapping the bar), positive: too high
            if (Math.Abs(deltaPx) > 0.5) pop.VerticalOffset += deltaPx / scale;
        }
        catch { /* popup closed meanwhile */ }
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

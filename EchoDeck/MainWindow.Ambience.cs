using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace EchoDeck;

// Adaptive colours for the music view (the cover's colours drive text, hover, selection, highlight and icon colours so
// everything stays readable on any cover) and the responsive layout of the player when the queue / side bar are open.
public partial class MainWindow
{
    // ------------------------------------------------------------------ adaptive colours from the cover

    /// <summary>The theme brushes that the side bar, queue, bottom bar and music view replace while a song's colours fill the window.</summary>
    private static readonly string[] AmbientKeys =
    {
        "TextBrush", "Fg1Brush", "MutedBrush", "Fg3Brush", "RaisedBrush", "SlateBrush", "TrackBrush",
        "AccentBrush", "AccentHoverBrush", "AccentMidBrush", "AccentDimBrush", "AccentSoftBrush", "OnAccentBrush",
    };

    // Brushes are never changed after they are made: WPF freezes some of them (the "Cannot animate ... because the object is sealed
    // or frozen" crash), so a new colour means new, frozen brushes put into the resources, not a colour animation on an old one.
    private readonly Dictionary<string, Color> _ambientColours = new();
    private readonly HashSet<FrameworkElement> _skinned = new();
    private readonly HashSet<FrameworkElement> _skinnedNeutral = new();   // the bottom bar and title bar: cover-tinted text and hover, but the app's own accent
    private Color _railColour = Color.FromArgb(0x8C, 0x10, 0x10, 0x14);
    private Color _barColour = Color.FromArgb(0x99, 0x10, 0x10, 0x14);
    private Color _menuColour = Color.FromRgb(0x16, 0x14, 0x1A);
    private IReadOnlyList<Color>? _ambientPalette;

    private static SolidColorBrush Frozen(Color c)
    {
        var brush = new SolidColorBrush(c);
        brush.Freeze();
        return brush;
    }

    /// <summary>The cover's colours changed (a new song, or its picture arrived).</summary>
    private void SetAmbientPalette(IReadOnlyList<Color> palette)
    {
        try
        {
            _ambientPalette = palette;
            UpdateAmbientColours(animate: true);
        }
        catch (Exception ex)
        {
            AppLog.Write("Ambient colours", ex);   // the colours are decoration: never let them crash the player
        }
    }

    private static Color Mix(Color a, Color b, double t) => Color.FromRgb(
        (byte)Math.Round(a.R + (b.R - a.R) * t),
        (byte)Math.Round(a.G + (b.G - a.G) * t),
        (byte)Math.Round(a.B + (b.B - a.B) * t));

    /// <summary>WCAG relative luminance (0 = black, 1 = white).</summary>
    private static double RelativeLuminance(Color c)
    {
        static double Channel(byte v)
        {
            double s = v / 255.0;
            return s <= 0.03928 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4);
        }
        return 0.2126 * Channel(c.R) + 0.7152 * Channel(c.G) + 0.0722 * Channel(c.B);
    }

    private void UpdateAmbientColours(bool animate)
    {
        Color seed = _ambientPalette is { Count: > 0 } ? _ambientPalette[0] : (Color)FindResource("AccentColor");
        CoverPalette.ToHsv(seed.R, seed.G, seed.B, out double hue, out double sat, out _);

        // the highlight: the cover's own hue, bright and clean; a grey cover gets a soft white. Always light enough to be read on the
        // dark glass (lightened until its luminance is high enough), so it is visible whatever the cover looks like.
        bool grey = sat < 0.16;
        Color accent = CoverPalette.FromHsv(hue, grey ? 0.06 : Math.Clamp(sat, 0.40, 0.85), 0.98);
        for (int i = 0; i < 8 && RelativeLuminance(accent) < 0.45; i++) accent = Mix(accent, Colors.White, 0.12);

        Color onAccent = RelativeLuminance(accent) > 0.40 ? Color.FromRgb(0x0B, 0x13, 0x20) : Colors.White;

        _ambientColours["TextBrush"] = Color.FromRgb(0xF4, 0xF4, 0xF6);
        _ambientColours["Fg1Brush"] = Color.FromArgb(0xDC, 0xFF, 0xFF, 0xFF);
        _ambientColours["MutedBrush"] = Color.FromArgb(0xC4, 0xFF, 0xFF, 0xFF);
        _ambientColours["Fg3Brush"] = Color.FromArgb(0x96, 0xFF, 0xFF, 0xFF);
        _ambientColours["RaisedBrush"] = Color.FromArgb(0x26, 0xFF, 0xFF, 0xFF);    // hover and cards: a soft lift of light
        _ambientColours["SlateBrush"] = Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF);
        _ambientColours["TrackBrush"] = Color.FromArgb(0x38, 0xFF, 0xFF, 0xFF);
        _ambientColours["AccentBrush"] = accent;
        _ambientColours["AccentHoverBrush"] = Mix(accent, Colors.White, 0.28);
        _ambientColours["AccentMidBrush"] = Mix(accent, Colors.Black, 0.30);
        _ambientColours["AccentDimBrush"] = Color.FromArgb(0x5C, accent.R, accent.G, accent.B);    // selected row / pressed
        _ambientColours["AccentSoftBrush"] = Color.FromArgb(0x3A, accent.R, accent.G, accent.B);   // bundles, the song that is playing
        _ambientColours["OnAccentBrush"] = onAccent;

        // the side bar, queue and bottom bar: a dark glass in the cover's hue, so white text always has the contrast it needs
        Color tint = CoverPalette.FromHsv(hue, grey ? 0.0 : Math.Min(sat, 0.70), 0.09);
        _railColour = Color.FromArgb(0x8C, tint.R, tint.G, tint.B);
        _barColour = Color.FromArgb(0x99, tint.R, tint.G, tint.B);
        // menus are opaque (a translucent popup window would show the desktop through it): the same hue, a little lighter
        _menuColour = CoverPalette.FromHsv(hue, grey ? 0.0 : Math.Min(sat, 0.55), 0.16);

        foreach (var el in _skinned) PutAmbientResources(el.Resources, _skinnedNeutral.Contains(el));
        ApplyTints(animate);
    }

    private static bool IsAccentKey(string key) => key.StartsWith("Accent", StringComparison.Ordinal) || key == "OnAccentBrush";

    private void PutAmbientResources(ResourceDictionary resources, bool neutral = false)
    {
        foreach (var key in AmbientKeys)
        {
            if (neutral && IsAccentKey(key)) continue;   // keeps the theme's accent colour
            if (_ambientColours.TryGetValue(key, out var colour)) resources[key] = Frozen(colour);
        }
    }

    /// <summary>The glass behind the side bar, queue and bottom bar (only while the cover's colours fill the window).</summary>
    private void ApplyTints(bool animate)
    {
        if (!_ambience) return;
        SetTint(Rail, _railColour, animate);
        SetTint(SidebarPanel, _railColour, animate);
        if (!_isFullscreen) SetTint(ControlBar, _barColour, animate);
    }

    private static void SetTint(Border el, Color target, bool animate)
    {
        var from = (el.Background as SolidColorBrush)?.Color;
        if (!animate || from == null || from.Value == target)
        {
            el.Background = Frozen(target);
            return;
        }

        // a fresh brush that nobody else shares: safe to animate
        var live = new SolidColorBrush(from.Value);
        el.Background = live;
        live.BeginAnimation(SolidColorBrush.ColorProperty,
            new ColorAnimation(target, TimeSpan.FromMilliseconds(900)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseInOut } });
    }

    /// <summary>
    /// Gives an element (and everything inside it) the adaptive colours, or takes them away again. Text that does not name its own
    /// colour takes it from the element's Foreground, so that is pointed at the same adaptive brush.
    /// </summary>
    private void SetAmbientSkin(FrameworkElement el, bool apply, bool neutral = false)
    {
        bool has = _skinned.Contains(el);
        if (apply == has) return;

        if (apply)
        {
            if (_ambientColours.Count == 0) UpdateAmbientColours(animate: false);
            _skinned.Add(el);
            if (neutral) _skinnedNeutral.Add(el);
            PutAmbientResources(el.Resources, neutral);
            el.SetResourceReference(TextElement.ForegroundProperty, "TextBrush");
        }
        else
        {
            _skinned.Remove(el);
            _skinnedNeutral.Remove(el);
            foreach (var key in AmbientKeys) el.Resources.Remove(key);
            el.ClearValue(TextElement.ForegroundProperty);
        }

        // the drawn-by-hand controls read their brushes only when they paint
        SeekBar.InvalidateVisual();
        WaveBar.InvalidateVisual();
    }

    /// <summary>Gives a right-click menu the music view's look: the cover's hue as its (opaque) background, and the adaptive text and highlight colours.</summary>
    private void SkinMenu(ContextMenu menu)
    {
        if (!(_currentIsAudio && !_libraryVisible && MusicPanel.Visibility == Visibility.Visible)) return;
        try
        {
            if (_ambientColours.Count == 0) UpdateAmbientColours(animate: false);
            PutAmbientResources(menu.Resources);
            menu.Resources["PanelBrush"] = Frozen(_menuColour);
        }
        catch (Exception ex) { AppLog.Write("Menu colours", ex); }
    }

    /// <summary>The bottom bar's background after the floating bar goes away: the glass while a song's colours fill the window, else the theme's.</summary>
    private void RestoreBarBackground()
    {
        if (_ambience) ControlBar.Background = Frozen(_barColour);
        else ControlBar.SetResourceReference(Border.BackgroundProperty, "BgBrush");
    }

    // ------------------------------------------------------------------ responsive layout

    /// <summary>The queue / playlists panel is narrower in a narrow window, so the music view keeps room to breathe.</summary>
    private double SidebarWidth => Math.Clamp(ActualWidth * 0.27, 250, 330);

    private void InitResponsive()
    {
        SizeChanged += (_, _) => ApplyResponsiveLayout();
        MusicPanel.SizeChanged += (_, _) => { RelayoutLyrics(); UpdateTaskbarClip(); };
        UpdateAmbientColours(animate: false);
    }

    private void ApplyResponsiveLayout()
    {
        if (SidebarPanel.Visibility == Visibility.Visible && !UiAnim.IsRunning(SidebarPanel))
        {
            double width = SidebarWidth;
            if (Math.Abs(SidebarPanel.Width - width) > 0.5) SidebarPanel.Width = width;
        }
        RelayoutLyrics();
    }

    /// <summary>The lyrics column follows the room the music view has (it is not a fixed width any more, so nothing is cut off).</summary>
    private void RelayoutLyrics()
    {
        if (!_lyricsOpen || UiAnim.IsRunning(LyricsCol)) return;

        double full = ComputeLyricsColumn();
        if (Math.Abs(LyricsCol.Width.Value - full) > 0.5) LyricsCol.Width = new GridLength(full);
        double panel = Math.Max(120, full - LyricsPanelMargin);
        if (Math.Abs(LyricsPanel.Width - panel) > 0.5) LyricsPanel.Width = panel;
        LyricsPanel.Opacity = 1;
    }

    // ------------------------------------------------------------------ taskbar preview

    private ClipRect? _lastTaskbarClip;

    /// <summary>
    /// The taskbar preview of the music view shows the cover, the visualiser and the title (a big, readable picture) instead of
    /// the whole window shrunk to a thumbnail. Everywhere else the preview shows the window as usual.
    /// </summary>
    private void UpdateTaskbarClip()
    {
        if (_taskbar == null || !_cfg.TaskbarControls) return;
        try
        {
            ClipRect? clip = null;
            if (!_anon && _currentIsAudio && !_libraryVisible && MusicPanel.Visibility == Visibility.Visible &&
                MusicPanel.ActualWidth > 80 && MusicPanel.ActualHeight > 80)
            {
                double cover = Math.Max(0, MusicPanel.ActualWidth - (_lyricsOpen ? LyricsCol.Width.Value : 0));
                var origin = MusicPanel.TranslatePoint(new Point(0, 0), this);
                double dpi = VisualTreeHelper.GetDpi(this).DpiScaleX;
                var rect = new ClipRect
                {
                    Left = (int)(origin.X * dpi),
                    Top = (int)(origin.Y * dpi),
                    Right = (int)((origin.X + cover) * dpi),
                    Bottom = (int)((origin.Y + MusicPanel.ActualHeight) * dpi),
                };
                if (rect.Right - rect.Left > 80 && rect.Bottom - rect.Top > 80) clip = rect;
            }

            if (Nullable.Equals(clip, _lastTaskbarClip)) return;
            _lastTaskbarClip = clip;
            _taskbar.SetClip(clip);
        }
        catch { /* the preview is a nicety */ }
    }
}

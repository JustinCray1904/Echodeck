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

    // One brush object per key, kept for the whole run: elements refer to them through DynamicResource, so animating a brush's
    // colour recolours everything that uses it, smoothly, without looking any resource up again.
    private readonly Dictionary<string, SolidColorBrush> _ambient = new();
    private readonly SolidColorBrush _railTint = new(Color.FromArgb(0x8C, 0x10, 0x10, 0x14));
    private readonly SolidColorBrush _barTint = new(Color.FromArgb(0x99, 0x10, 0x10, 0x14));
    private IReadOnlyList<Color>? _ambientPalette;

    private SolidColorBrush AmbientBrush(string key)
    {
        if (!_ambient.TryGetValue(key, out var brush))
        {
            brush = new SolidColorBrush(Colors.White);
            _ambient[key] = brush;
        }
        return brush;
    }

    private static void FadeTo(SolidColorBrush brush, Color target, bool animate)
    {
        if (brush.Color == target && !brush.HasAnimatedProperties) return;
        if (!animate)
        {
            brush.BeginAnimation(SolidColorBrush.ColorProperty, null);
            brush.Color = target;
            return;
        }
        brush.BeginAnimation(SolidColorBrush.ColorProperty,
            new ColorAnimation(target, TimeSpan.FromMilliseconds(900)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseInOut } });
    }

    /// <summary>The cover's colours changed (a new song, or its picture arrived).</summary>
    private void SetAmbientPalette(IReadOnlyList<Color> palette)
    {
        _ambientPalette = palette;
        UpdateAmbientColours(animate: true);
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

        void Set(string key, Color c) => FadeTo(AmbientBrush(key), c, animate);

        Set("TextBrush", Color.FromRgb(0xF4, 0xF4, 0xF6));
        Set("Fg1Brush", Color.FromArgb(0xDC, 0xFF, 0xFF, 0xFF));
        Set("MutedBrush", Color.FromArgb(0xC4, 0xFF, 0xFF, 0xFF));
        Set("Fg3Brush", Color.FromArgb(0x96, 0xFF, 0xFF, 0xFF));
        Set("RaisedBrush", Color.FromArgb(0x26, 0xFF, 0xFF, 0xFF));    // hover and cards: a soft lift of light
        Set("SlateBrush", Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF));
        Set("TrackBrush", Color.FromArgb(0x38, 0xFF, 0xFF, 0xFF));
        Set("AccentBrush", accent);
        Set("AccentHoverBrush", Mix(accent, Colors.White, 0.28));
        Set("AccentMidBrush", Mix(accent, Colors.Black, 0.30));
        Set("AccentDimBrush", Color.FromArgb(0x5C, accent.R, accent.G, accent.B));    // selected row / pressed
        Set("AccentSoftBrush", Color.FromArgb(0x3A, accent.R, accent.G, accent.B));   // bundles, the song that is playing
        Set("OnAccentBrush", onAccent);

        // the side bar, queue and bottom bar: a dark glass in the cover's hue, so white text always has the contrast it needs
        Color tint = CoverPalette.FromHsv(hue, grey ? 0.0 : Math.Min(sat, 0.70), 0.09);
        FadeTo(_railTint, Color.FromArgb(0x8C, tint.R, tint.G, tint.B), animate);
        FadeTo(_barTint, Color.FromArgb(0x99, tint.R, tint.G, tint.B), animate);
    }

    /// <summary>
    /// Gives an element (and everything inside it) the adaptive colours, or takes them away again. Text that does not name its own
    /// colour takes it from the element's Foreground, so that is pointed at the same adaptive brush.
    /// </summary>
    private void SetAmbientSkin(FrameworkElement el, bool apply)
    {
        bool has = el.Resources.Contains("TextBrush");
        if (apply == has) return;

        if (apply)
        {
            foreach (var key in AmbientKeys) el.Resources[key] = AmbientBrush(key);
            el.SetResourceReference(TextElement.ForegroundProperty, "TextBrush");
        }
        else
        {
            foreach (var key in AmbientKeys) el.Resources.Remove(key);
            el.ClearValue(TextElement.ForegroundProperty);
        }

        // the drawn-by-hand controls read their brushes only when they paint
        SeekBar.InvalidateVisual();
        WaveBar.InvalidateVisual();
    }

    /// <summary>The bottom bar's background after the floating bar goes away: the glass while a song's colours fill the window, else the theme's.</summary>
    private void RestoreBarBackground()
    {
        if (_ambience) ControlBar.Background = _barTint;
        else ControlBar.SetResourceReference(Border.BackgroundProperty, "BgBrush");
    }

    // ------------------------------------------------------------------ responsive layout

    /// <summary>The queue / playlists panel is narrower in a narrow window, so the music view keeps room to breathe.</summary>
    private double SidebarWidth => Math.Clamp(ActualWidth * 0.27, 250, 330);

    private void InitResponsive()
    {
        SizeChanged += (_, _) => ApplyResponsiveLayout();
        MusicPanel.SizeChanged += (_, _) => RelayoutLyrics();
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
}

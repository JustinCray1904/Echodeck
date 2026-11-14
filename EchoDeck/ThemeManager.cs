using System.Windows;
using System.Windows.Media;
using Microsoft.Win32;

namespace EchoDeck;

/// <summary>
/// Swaps the app-wide colour brushes at runtime. Every style/window refers to them with DynamicResource,
/// so replacing the entries in Application.Resources re-themes everything that is open.
/// Light blue ("Sky") is the default accent; Anonymous Mode forces dark with a violet accent.
/// </summary>
public static class ThemeManager
{
    public sealed record AccentInfo(string Name, string Hex);

    public static readonly AccentInfo[] Accents =
    {
        new("Sky",    "#5BB5FF"),
        new("Blue",   "#4C7DFF"),
        new("Teal",   "#2FC9C0"),
        new("Green",  "#46C26B"),
        new("Yellow", "#E3B93B"),
        new("Orange", "#E08A2E"),
        new("Red",    "#EE5D5D"),
        new("Purple", "#9B7BFF"),
        new("Pink",   "#E65DA2"),
    };

    private const string AnonymousAccentHex = "#A78BFA";

    public static bool IsDark { get; private set; } = true;

    public static Color AccentColorOf(int index)
    {
        if (index < 0 || index >= Accents.Length) index = 0;
        return Parse(Accents[index].Hex);
    }

    public static bool SystemUsesLightTheme()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is int v && v == 1;
        }
        catch { return false; }
    }

    public static void Apply(ThemeMode mode, int accentIndex, bool anonymous = false)
    {
        var res = Application.Current?.Resources;
        if (res == null) return;

        bool dark = anonymous || mode != ThemeMode.Light && (mode != ThemeMode.System || !SystemUsesLightTheme());
        bool glass = !anonymous && mode == ThemeMode.LiquidGlass;
        IsDark = dark;

        Color accent = anonymous ? Parse(AnonymousAccentHex) : AccentColorOf(accentIndex);

        Color bg, rail, panel, track, raised, slate, text, fg1, muted, fg3, card, danger;
        if (dark)
        {
            bg = Parse("#1B1B1B"); rail = Parse("#191919"); panel = Parse("#0E0E0E"); track = Parse("#090909");
            raised = Parse("#222222"); slate = Parse("#373737"); text = Parse("#D9D9D9"); fg1 = Parse("#969696");
            muted = Parse("#8F8F8F"); fg3 = Parse("#6B6B6B"); card = Parse("#151515"); danger = Parse("#FF5C6C");

            if (!anonymous && mode == ThemeMode.Midnight)
            {
                bg = Parse("#0B1020"); rail = Parse("#0A0E1B"); panel = Parse("#070B16"); track = Parse("#04060D");
                raised = Parse("#141B31"); slate = Parse("#27315A"); text = Parse("#DCE3F7"); fg1 = Parse("#8D99BC");
                muted = Parse("#8691B5"); fg3 = Parse("#5D6890"); card = Parse("#0C1226");
            }
            else if (!anonymous && mode == ThemeMode.Amoled)
            {
                // true black behind, but the panels, rail and controls are lifted a little so every layer can be told apart
                bg = Parse("#000000"); rail = Parse("#0D0D0D"); panel = Parse("#131313"); track = Parse("#222222");
                raised = Parse("#1E1E1E"); slate = Parse("#3C3C3C"); text = Parse("#EDEDED"); fg1 = Parse("#B6B6B6");
                muted = Parse("#A8A8A8"); fg3 = Parse("#808080"); card = Parse("#171717");
            }
            else if (glass)
            {
                // "Liquid glass": frosted, translucent panels with a bright rim of light, sitting on a colourful backdrop (see below).
                // The panels themselves are gradients (set after the flat colours), the colours here are the supporting ones.
                bg = Parse("#14163C"); rail = Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF); panel = Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF);
                track = Color.FromArgb(0x78, 0x04, 0x05, 0x1C); raised = Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF);
                slate = Color.FromArgb(0x70, 0xFF, 0xFF, 0xFF); text = Parse("#FFFFFF"); fg1 = Parse("#DDE3F8");
                muted = Parse("#C8D0EE"); fg3 = Parse("#A6AFD8"); card = Color.FromArgb(0x26, 0xFF, 0xFF, 0xFF);
            }
        }
        else
        {
            // more contrast than before: a cooler grey window, lighter panels on it, clearer borders and darker text
            bg = Parse("#D3D9E3"); rail = Parse("#E7EBF2"); panel = Parse("#EEF1F6"); track = Parse("#B7C0CE");
            raised = Parse("#D9DFE9"); slate = Parse("#A5B0C0"); text = Parse("#0E1319"); fg1 = Parse("#313C4E");
            muted = Parse("#404B5C"); fg3 = Parse("#5F6C7E"); card = Parse("#F7F9FC"); danger = Parse("#D92D3F");
            accent = Blend(accent, Colors.Black, 0.22);   // keep the accent readable on light surfaces
        }

        Color accentHover = dark ? Blend(accent, Colors.White, 0.18) : Blend(accent, Colors.Black, 0.12);
        Color accentMid   = Blend(track, accent, dark ? 0.60 : 0.55);   // seek / volume fill
        Color accentDim   = Blend(bg, accent, dark ? 0.22 : 0.20);      // selection, pressed
        Color accentSoft  = Color.FromArgb(dark ? (byte)0x2E : (byte)0x26, accent.R, accent.G, accent.B); // action bundles
        Color onAccent    = Luminance(accent) > 0.50 ? Parse("#0B1320") : Colors.White;

        Set(res, "Bg", bg); Set(res, "Rail", rail); Set(res, "Panel", panel); Set(res, "Track", track);
        Set(res, "Raised", raised); Set(res, "Slate", slate); Set(res, "Text", text); Set(res, "Fg1", fg1);
        Set(res, "Muted", muted); Set(res, "Fg3", fg3); Set(res, "Card", card); Set(res, "Danger", danger);
        Set(res, "Accent", accent); Set(res, "AccentHover", accentHover); Set(res, "AccentMid", accentMid);
        Set(res, "AccentDim", accentDim); Set(res, "AccentSoft", accentSoft); Set(res, "OnAccent", onAccent);
        Set(res, "Scrim", Color.FromArgb(0xCC, bg.R, bg.G, bg.B));

        // the window background: a flat colour, or for liquid glass a colourful backdrop the glass panels sit on
        if (glass)
        {
            res["BgBrush"] = GlassBackdrop();
            var sheen = GlassPanel(0x58, 0x20);     // brighter on top, softer below: light falling on a frosted pane
            res["PanelBrush"] = sheen;
            res["RailBrush"] = GlassPanel(0x4C, 0x1C);
        }
    }

    /// <summary>Soft blobs of pink, blue, cyan and violet on a deep base: what the glass panels refract.</summary>
    private static Brush GlassBackdrop()
    {
        var group = new DrawingGroup();
        group.Children.Add(new GeometryDrawing(new SolidColorBrush(Parse("#0E1030")), null, new RectangleGeometry(new Rect(0, 0, 100, 100))));

        void Blob(double cx, double cy, double r, string hex, byte alpha)
        {
            var c = Parse(hex);
            var glow = new RadialGradientBrush { Center = new Point(0.5, 0.5), GradientOrigin = new Point(0.5, 0.5), RadiusX = 0.5, RadiusY = 0.5 };
            glow.GradientStops.Add(new GradientStop(Color.FromArgb(alpha, c.R, c.G, c.B), 0.0));
            glow.GradientStops.Add(new GradientStop(Color.FromArgb(0, c.R, c.G, c.B), 1.0));
            group.Children.Add(new GeometryDrawing(glow, null, new RectangleGeometry(new Rect(cx - r, cy - r, 2 * r, 2 * r))));
        }

        Blob(8, 10, 46, "#FF4FA3", 0xA0);     // pink, top left
        Blob(96, 8, 48, "#3C78FF", 0x94);     // blue, top right
        Blob(55, 104, 52, "#28D6FF", 0x88);   // cyan, bottom
        Blob(52, 42, 34, "#8A5CFF", 0x70);    // violet, middle
        Blob(4, 92, 30, "#B04CFF", 0x60);     // purple, bottom left
        group.Freeze();

        var brush = new DrawingBrush(group)
        {
            Stretch = Stretch.Fill,
            Viewbox = new Rect(0, 0, 100, 100),
            ViewboxUnits = BrushMappingMode.Absolute,
        };
        RenderOptions.SetCachingHint(brush, CachingHint.Cache);
        brush.Freeze();
        return brush;
    }

    private static Brush GlassPanel(byte topAlpha, byte bottomAlpha)
    {
        var b = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(0, 1) };
        b.GradientStops.Add(new GradientStop(Color.FromArgb(topAlpha, 0xFF, 0xFF, 0xFF), 0.0));
        b.GradientStops.Add(new GradientStop(Color.FromArgb(bottomAlpha, 0xE6, 0xEA, 0xFF), 1.0));
        b.Freeze();
        return b;
    }

    private static void Set(ResourceDictionary res, string name, Color c)
    {
        var brush = new SolidColorBrush(c);
        brush.Freeze();
        res[name + "Brush"] = brush;
        res[name + "Color"] = c;
    }

    public static Color Parse(string hex) => (Color)ColorConverter.ConvertFromString(hex);

    private static Color Blend(Color a, Color b, double t) => Color.FromRgb(
        (byte)Math.Round(a.R + (b.R - a.R) * t),
        (byte)Math.Round(a.G + (b.G - a.G) * t),
        (byte)Math.Round(a.B + (b.B - a.B) * t));

    private static double Luminance(Color c) => (0.2126 * c.R + 0.7152 * c.G + 0.0722 * c.B) / 255.0;
}

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

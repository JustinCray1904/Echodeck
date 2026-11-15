using System.Net;
using System.Text.RegularExpressions;

namespace EchoDeck;

/// <summary>
/// Tidies the names shown for songs: "yt1z.net - Polo G - Flex (Official Audio) ft. Juice WRLD (320 KBps)" becomes
/// "Polo G - Flex ft. Juice WRLD". Only what is shown changes: the files are never renamed. Switched by the "Tidy file names" setting.
/// </summary>
internal static class DisplayNames
{
    private static readonly Regex Site = new(@"^\s*(?:[\w-]+\.)+(?:net|com|app|xyz|org|io|cc|to|me|site|co|ca|info|ws|tv|pro)\s*[-–—_:]\s*", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex Bracketed = new(@"\s*[\(\[][^\)\]]*(?:official|video|audio|lyric|visuali[sz]er|prod\.?|kbps|\d{3,4}p\b|\bhd\b|\bhq\b|remaster|clip|music video|full song)[^\)\]]*[\)\]]", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex VideoId = new(@"\s*\[[A-Za-z0-9_-]{11}\]", RegexOptions.Compiled);
    private static readonly Regex TrailingQuality = new(@"\s*[\(\[]?\s*\d{2,3}\s*kbps\s*[\)\]]?\s*$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex PipeTail = new(@"\s*[|｜]\s*(?:lyrics?|official|audio|video).*$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex Spaces = new(@"\s{2,}", RegexOptions.Compiled);

    /// <summary>A name without its extension.</summary>
    public static string Clean(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return name ?? "";
        if (!AppSettings.Current.CleanTitles) return name;

        string s = WebUtility.HtmlDecode(name);
        s = Site.Replace(s, "");
        s = VideoId.Replace(s, "");
        s = Bracketed.Replace(s, "");
        s = PipeTail.Replace(s, "");
        s = TrailingQuality.Replace(s, "");
        s = Spaces.Replace(s, " ").Trim(' ', '-', '_', '.', '–', '—');
        return s.Length >= 2 ? s : name;   // never end up with nothing
    }

    /// <summary>A file name with its extension (the extension is kept as it is).</summary>
    public static string CleanFileName(string fileName)
    {
        string ext = Path.GetExtension(fileName);
        if (ext.Length is < 2 or > 5) return Clean(fileName);
        return Clean(Path.GetFileNameWithoutExtension(fileName)) + ext;
    }
}

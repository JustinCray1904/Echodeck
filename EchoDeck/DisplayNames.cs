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
    private static readonly Regex Bracketed = new(@"\s*[\(\[【「『][^\)\]】」』]*(?:official|video|audio|lyric|visuali[sz]er|prod\.?|kbps|\d{3,4}p\b|\bhd\b|\bhq\b|\b[48]k\b|\bmv\b|\bm/v\b|remaster|clip|music video|full song|explicit|out now|free download)[^\)\]】」』]*[\)\]】」』]", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex VideoId = new(@"\s*\[[A-Za-z0-9_-]{11}\]", RegexOptions.Compiled);
    private static readonly Regex TrailingQuality = new(@"\s*[\(\[]?\s*\d{2,3}\s*kbps\s*[\)\]]?\s*$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex PipeTail = new(@"\s*[|｜]\s*(?:lyrics?|official|audio|video|visuali[sz]er|music|clip|mv|hd|hq|4k|full|new|explicit).*$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // Noise at the END of a name that is NOT in brackets: "Flex Official Music Video", "Flex - Official Audio", "Flex Lyric Video", "Flex HD".
    private static readonly Regex StrongTail = new(@"(?:^|[\s\-–—|:~/])+(?:official\s+(?:(?:music|lyrics?|lyrical|hd|4k)\s+)?(?:video|audio|visuali[sz]er|mv|m/v|clip)|(?:music|lyrics?|lyrical|visuali[sz]er)\s+video|(?:with|w/)\s+lyrics|full\s+(?:song|audio|video)|(?:hd|hq|4k|uhd)(?:\s+(?:audio|video))?)\s*$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // A bare "Lyrics", "Audio", "Video" or "Visualizer" at the end. Whether it is noise depends on what comes before it (see StripTails).
    private static readonly Regex WeakTail = new(@"(?<sep>\s+[-–—]\s+|\s+)(?:lyrics?|audio|video|visuali[sz]er)\s*$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // "#shorts", "#lyrics" at the end (but never "#1 Hit": a hashtag must start with a letter)
    private static readonly Regex Hashtags = new(@"(?:\s+#(?!\d)[\p{L}_][\p{L}\p{Nd}_]*)+\s*$", RegexOptions.Compiled);
    private static readonly Regex Spaces = new(@"\s{2,}", RegexOptions.Compiled);

    /// <summary>A name without its extension, tidied if the "Tidy song names" setting is on.</summary>
    public static string Clean(string? name) =>
        string.IsNullOrWhiteSpace(name) ? name ?? "" : AppSettings.Current.CleanTitles ? Tidy(name) : name;

    /// <summary>
    /// The tidy form of a name, whatever the setting says: this is what is used to look songs up (lyrics, covers), so a title like
    /// "yt1z.net - Polo G - Flex (Official Audio) (320 KBps)" is searched for as "Polo G - Flex".
    /// </summary>
    public static string Tidy(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return name ?? "";

        string s = WebUtility.HtmlDecode(name);
        s = Site.Replace(s, "");
        s = VideoId.Replace(s, "");
        s = Bracketed.Replace(s, "");
        s = PipeTail.Replace(s, "");
        s = TrailingQuality.Replace(s, "");
        s = Hashtags.Replace(s, "");
        s = StripTails(s);
        s = Spaces.Replace(s, " ").Trim(' ', '-', '_', '.', '–', '—');
        return s.Length >= 2 ? s : name;   // never end up with nothing
    }

    private static bool HasDash(string s) => s.Contains(" - ") || s.Contains(" – ") || s.Contains(" — ");

    /// <summary>
    /// Takes "Official Music Video", "Lyric Video", "Visualizer", "HD" ... off the end, also when they are not in brackets. A bare
    /// "Audio" / "Video" / "Lyrics" goes only when it is clearly an add-on: "Flex Audio" becomes "Flex", "Artist - Song - Audio" becomes
    /// "Artist - Song", but "India.Arie - Video" stays (there the word is the song's title).
    /// </summary>
    private static string StripTails(string s)
    {
        for (int i = 0; i < 4; i++)
        {
            string before = s;
            string strong = StrongTail.Replace(s, "");
            if (strong != s)
            {
                s = strong.TrimEnd(' ', '-', '–', '—', '|', ':', '~', '_');   // go round again: "Official Music Video HD" needs two passes
                continue;
            }

            var m = WeakTail.Match(s);
            if (m.Success)
            {
                string rest = s.Substring(0, m.Index).TrimEnd();
                bool dash = m.Groups["sep"].Value.IndexOfAny(new[] { '-', '–', '—' }) >= 0;
                if (rest.Length >= 2 && (!dash || HasDash(rest))) s = rest;
            }

            s = s.TrimEnd(' ', '-', '–', '—', '|', ':', '~', '_');
            if (s == before) break;
        }
        return s;
    }

    /// <summary>A file name with its extension (the extension is kept as it is).</summary>
    public static string CleanFileName(string fileName)
    {
        string ext = Path.GetExtension(fileName);
        if (ext.Length is < 2 or > 5) return Clean(fileName);
        return Clean(Path.GetFileNameWithoutExtension(fileName)) + ext;
    }
}

/// <summary>Comparing song titles and artist names from different sources (tags, file names, lyrics and cover databases).</summary>
internal static class TextMatch
{
    private static readonly Regex InBrackets = new(@"[\(\[][^\)\]]*[\)\]]", RegexOptions.Compiled);
    private static readonly Regex Feature = new(@"\s*[\(\[]?\s*\b(?:feat\.?|ft\.?|featuring)\s+.*$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>"Flex ft. Juice WRLD" and "Flex (feat. Juice WRLD)" are both "Flex".</summary>
    public static string StripFeature(string title) => Feature.Replace(title ?? "", "").Trim();

    /// <summary>Letters and digits only, lower case, without bracketed extras or a "feat." tail.</summary>
    public static string Norm(string? s)
    {
        s = InBrackets.Replace(s ?? "", "");
        s = Feature.Replace(s, "");
        return Regex.Replace(s, @"[^\p{L}\p{Nd}]", "").ToLowerInvariant();
    }

    /// <summary>The same title: equal after tidying, or (for longer titles) differing only by a typo or two.</summary>
    public static bool SameTitle(string? a, string? b)
    {
        string x = Norm(a), y = Norm(b);
        if (x.Length == 0 || y.Length == 0) return false;
        if (x == y) return true;
        return Math.Min(x.Length, y.Length) >= 6 && Similarity(x, y) >= 0.88;
    }

    /// <summary>The wanted artist is the found one, or part of it ("Juice WRLD" in "Juice WRLD & Young Thug"). Unknown wanted artist matches anything.</summary>
    public static bool SameArtist(string? wanted, string? found)
    {
        string w = Norm(wanted), f = Norm(found);
        if (w.Length == 0) return true;
        if (f.Length == 0) return false;
        if (w == f) return true;
        if (Math.Min(w.Length, f.Length) >= 3 && (f.Contains(w) || w.Contains(f))) return true;
        return Similarity(w, f) >= 0.85;
    }

    /// <summary>1 = identical, 0 = nothing in common (edit distance relative to the longer text).</summary>
    public static double Similarity(string a, string b)
    {
        if (a.Length == 0 && b.Length == 0) return 1;
        int max = Math.Max(a.Length, b.Length);
        return 1.0 - Levenshtein(a, b) / (double)max;
    }

    private static int Levenshtein(string a, string b)
    {
        var previous = new int[b.Length + 1];
        var current = new int[b.Length + 1];
        for (int j = 0; j <= b.Length; j++) previous[j] = j;
        for (int i = 1; i <= a.Length; i++)
        {
            current[0] = i;
            for (int j = 1; j <= b.Length; j++)
            {
                int cost = a[i - 1] == b[j - 1] ? 0 : 1;
                current[j] = Math.Min(Math.Min(current[j - 1] + 1, previous[j] + 1), previous[j - 1] + cost);
            }
            (previous, current) = (current, previous);
        }
        return previous[b.Length];
    }
}

using System.Collections.Concurrent;
using System.Globalization;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace EchoDeck;

/// <summary>Word-level helpers for comparing the names of songs and artists. Pure functions.</summary>
internal static class SongText
{
    private static readonly HashSet<string> StopWords = new(StringComparer.Ordinal)
    {
        "the", "a", "an", "and", "feat", "ft", "featuring", "with", "vs", "x", "of"
    };

    private static readonly Regex Brackets = new(@"[\(\[【「『][^\)\]】」』]*[\)\]】」』]", RegexOptions.Compiled);
    private static readonly Regex ArtistSplit = new(@"\s*(?:&|,|;|/|\+|\bx\b|\band\b|\bfeat\.?|\bft\.?|\bfeaturing\b|\bwith\b|\bvs\.?)\s*", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex FeatClause = new(@"\b(?:feat\.?|ft\.?|featuring)\s+([^\)\]]+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex LeadingTrack = new(@"^\s*\d{1,3}\s*(?:[-._)]\s+|\s+[-–—]\s+)", RegexOptions.Compiled);
    private static readonly Regex TopicTail = new(@"\s*[-–—]\s*Topic\s*$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex VevoTail = new(@"\s*(?:-\s*)?VEVO\s*$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex OfficialTail = new(@"\s+Official\s*$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex Emoji = new(@"[\u2600-\u27BF\uD83C-\uDBFF\uDC00-\uDFFF\uFE0F]+", RegexOptions.Compiled);
    private static readonly Regex Spaces = new(@"\s{2,}", RegexOptions.Compiled);
    private static readonly Regex JunkArtist = new(@"^(?:unknown(?:\s+artist)?|<unknown>|various(?:\s+artists)?|va|n/?a|none|artist|youtube|audio|music)$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex JunkTitle = new(@"^(?:track\s*\d{1,3}|untitled|unknown(?:\s+title)?|<unknown>|audio\s*track(?:\s*\d+)?|title)$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>Versions of a song that are not the song itself (karaoke, tribute, remix ...): a catalogue entry like that counts against it.</summary>
    internal static readonly Regex Junk = new(@"\b(?:karaoke|instrumental|tribute|originally\s+performed|made\s+famous|piano\s+version|lullaby|8d|nightcore|slowed|sped\s+up|reverb|remix|cover|acoustic|live)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>"Love Story (Taylor's Version)" becomes "Love Story".</summary>
    internal static string StripBrackets(string? s) => Spaces.Replace(Brackets.Replace(s ?? "", " "), " ").Trim();

    /// <summary>The words of a text: lower case, no accents or apostrophes, without filler words like "the" and "feat". Never null.</summary>
    internal static List<string> Tokens(string? s)
    {
        var result = new List<string>();
        if (string.IsNullOrWhiteSpace(s)) return result;

        string text = s;
        try { text = s.Normalize(NormalizationForm.FormD); } catch (ArgumentException) { /* odd characters: use the text as it is */ }

        var sb = new StringBuilder(text.Length);
        foreach (char ch in text)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(ch) == UnicodeCategory.NonSpacingMark) continue;   // accents
            if (ch == '\'' || ch == '’' || ch == '`') continue;                                         // "Taylor's" is "taylors"
            sb.Append(char.IsLetterOrDigit(ch) ? char.ToLowerInvariant(ch) : ' ');
        }

        var all = sb.ToString().Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
        foreach (var w in all)
            if (!StopWords.Contains(w)) result.Add(w);

        // a title made only of filler words ("The The", "And") keeps them
        if (result.Count == 0) result.AddRange(all);
        return result;
    }

    /// <summary>The same word, or (for longer words) one typo apart.</summary>
    internal static bool SameToken(string a, string b)
    {
        if (a == b) return true;
        if (a.Length < 5 || b.Length < 5 || Math.Abs(a.Length - b.Length) > 1) return false;
        return TextMatch.Similarity(a, b) >= 0.8;
    }

    internal static bool ContainsToken(IReadOnlyList<string> words, string token)
    {
        for (int i = 0; i < words.Count; i++)
            if (SameToken(words[i], token)) return true;
        return false;
    }

    /// <summary>
    /// The names a catalogue entry is credited to, as word lists: the whole artist field, each name in it ("A &amp; B", "A, B", "A x B"),
    /// and the featured artists named in the title.
    /// </summary>
    internal static List<List<string>> ArtistSets(string artist, string title)
    {
        var sets = new List<List<string>>();

        void Add(string name)
        {
            var words = Tokens(name);
            if (words.Count > 0) sets.Add(words);
        }

        Add(artist);
        foreach (var part in ArtistSplit.Split(artist ?? "")) Add(part);
        foreach (Match m in FeatClause.Matches(title ?? ""))
        {
            Add(m.Groups[1].Value);
            foreach (var part in ArtistSplit.Split(m.Groups[1].Value)) Add(part);
        }
        return sets;
    }

    /// <summary>An artist name without "- Topic" (YouTube's auto channels), "VEVO" and "Official"; empty for "Unknown artist" and the like.</summary>
    internal static string CleanArtist(string? artist)
    {
        string a = (artist ?? "").Trim();
        if (a.Length == 0) return "";
        a = TopicTail.Replace(a, "");
        a = VevoTail.Replace(a, "");
        a = OfficialTail.Replace(a, "");
        a = Spaces.Replace(a, " ").Trim(' ', '-', '_', '–', '—');
        return JunkArtist.IsMatch(a) ? "" : a;
    }

    /// <summary>
    /// For look-ups only (never for what is shown): a leading track number ("03 - Flex"), emoji and "- Topic" go, on top of what
    /// <see cref="DisplayNames.Tidy"/> already removes.
    /// </summary>
    internal static string Polish(string? s)
    {
        string text = s ?? "";
        string p = Emoji.Replace(text, " ");
        p = LeadingTrack.Replace(p, "");
        p = TopicTail.Replace(p, "");
        p = Spaces.Replace(p, " ").Trim(' ', '-', '_', '–', '—', '|');
        return p.Length >= 1 ? p : text.Trim();
    }

    internal static (string Artist, string Title) SplitArtistTitle(string name)
    {
        var parts = name.Split(new[] { " - ", " – ", " — " }, 2, StringSplitOptions.None);
        return parts.Length == 2 && parts[0].Trim().Length > 0 && parts[1].Trim().Length > 0
            ? (parts[0].Trim(), parts[1].Trim())
            : ("", name);
    }

    /// <summary>Best guess of artist and title from a file name such as "yt1z.net - Juice WRLD - Ten Dirty (Official Video) [320 KBps]".</summary>
    internal static (string Artist, string Title) FromFileName(string path)
    {
        string name = Polish(DisplayNames.Tidy(Path.GetFileNameWithoutExtension(path) ?? ""));
        var split = SplitArtistTitle(name);
        return (CleanArtist(split.Artist), split.Title);
    }

    /// <summary>
    /// Who and what a song is, from the file alone (tags when they are good, else the tidied file name): the title is the same one the
    /// library shows, minus "(feat. ...)" because catalogues list the featured artist separately.
    /// </summary>
    internal static (string Artist, string Title) Local(string path, TagInfo tags)
    {
        string artist = CleanArtist(tags.Artist);
        string title = JunkTitle.IsMatch((tags.Title ?? "").Trim()) ? "" : Polish(DisplayNames.Tidy(tags.Title ?? ""));

        if (title.Length > 0)
        {
            var split = SplitArtistTitle(title);
            if (artist.Length == 0)
            {
                // a tag like "Polo G - Flex" with no artist tag
                artist = CleanArtist(split.Artist);
                title = split.Title;
            }
            else if (split.Artist.Length > 0)
            {
                // "Polo G - Flex" with the artist tag "Polo G": the artist is not part of the title ("Flex - Polo G" likewise)
                if (TextMatch.SameArtist(artist, split.Artist)) title = split.Title;
                else if (TextMatch.SameArtist(artist, split.Title)) title = split.Artist;
            }
        }

        if (title.Length == 0)
        {
            var fromName = FromFileName(path);
            title = fromName.Title;
            if (artist.Length == 0) artist = fromName.Artist;
        }
        else if (artist.Length == 0)
        {
            // the tags hold only a title ("Flex"): the file name ("Polo G - Flex.mp3") may know the artist, when it names the same song
            var fromName = FromFileName(path);
            if (fromName.Artist.Length > 0 && TextMatch.SameTitle(title, fromName.Title)) artist = fromName.Artist;
        }

        string bare = TextMatch.StripFeature(title);
        return (TextMatch.StripFeature(artist), bare.Length > 0 ? bare : title);
    }
}

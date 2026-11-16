using System.Collections.Concurrent;
using System.Globalization;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace EchoDeck;

/// <summary>
/// What a song really is, after the file's name and tags were cross-checked against online catalogues.
/// <see cref="Verified"/>: a catalogue entry matched (so <see cref="Artist"/> and <see cref="Title"/> are the catalogue's spelling);
/// otherwise they are just the tidied local guess. <see cref="Answered"/>: at least one catalogue answered (so "nothing found" is a real
/// result and not just "offline"). <see cref="CoverUrls"/>: the cover pictures that came with the matches, best first.
/// </summary>
internal sealed record SongIdentity(string Artist, string Title, string Album, double Seconds, IReadOnlyList<string> CoverUrls, bool Verified, bool Answered)
{
    /// <summary>The title without any bracketed part: "Love Story (Taylor's Version)" becomes "Love Story".</summary>
    public string TitleBare
    {
        get
        {
            string bare = SongText.StripBrackets(Title);
            return bare.Length > 0 ? bare : Title;
        }
    }
}

/// <summary>One song as a catalogue lists it.</summary>
internal sealed record SongCandidate(string Title, string Artist, string Album, double Seconds, string? Cover, int Rank);

/// <summary>What is known about the file, prepared once for comparing it with many catalogue entries.</summary>
internal sealed class SongQuery
{
    public string Artist { get; }
    public string Title { get; }
    public double Seconds { get; }

    /// <summary>The words of the tidied artist and title.</summary>
    public List<string> Clean { get; }

    /// <summary>All the words the file tells us anywhere (tags, file name, tidied name): a superset of <see cref="Clean"/>.</summary>
    public List<string> Raw { get; }

    /// <summary>The file itself is a remix / live / acoustic ... version, so catalogue entries like that are fine.</summary>
    public bool JunkWanted { get; }

    public SongQuery(string artist, string title, double seconds, string rawText)
    {
        Artist = artist ?? "";
        Title = title ?? "";
        Seconds = seconds;
        Clean = SongText.Tokens(Artist + " " + Title);
        Raw = SongText.Tokens((rawText ?? "") + " " + Artist + " " + Title);
        JunkWanted = SongText.Junk.IsMatch((rawText ?? "") + " " + Title);
    }
}

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

        string bare = TextMatch.StripFeature(title);
        return (TextMatch.StripFeature(artist), bare.Length > 0 ? bare : title);
    }
}

/// <summary>Decides whether a catalogue entry is the song the file holds.</summary>
internal static class SongMatcher
{
    /// <summary>The least score an entry needs to be accepted as this song.</summary>
    internal const double Accept = 75;

    /// <summary>
    /// Scores a catalogue entry against the file. False when it is not this song. The rules, all of which must hold:
    ///  1. every word of the entry's title is somewhere in what the file tells us (so "Love" is never taken for "Love Story");
    ///  2. the entry (title + artist) explains at least 70 % of the words of the tidied name (so "Flex" by someone else is not it);
    ///  3. the artist matches, when the file has one (the entry's artists, its featured artists, or any name in "A &amp; B");
    ///  4. with no artist known, the length must be within 10 s of the file's (or the entry must be the catalogue's first hit).
    /// The score then rewards word coverage, the artist, an exact title and a length close to the file's, and punishes karaoke, tribute,
    /// remix ... versions that the file is not.
    /// </summary>
    internal static bool TryScore(SongQuery q, SongCandidate c, out double score)
    {
        score = 0;
        if (c.Title.Length == 0 || q.Clean.Count == 0) return false;

        var titleWords = SongText.Tokens(SongText.StripBrackets(c.Title));
        if (titleWords.Count == 0) titleWords = SongText.Tokens(c.Title);
        if (titleWords.Count == 0) return false;

        foreach (var w in titleWords)
            if (!SongText.ContainsToken(q.Raw, w)) return false;

        var explained = SongText.Tokens(c.Title + " " + c.Artist);
        int hit = 0;
        foreach (var w in q.Clean)
            if (SongText.ContainsToken(explained, w)) hit++;
        double coverage = hit / (double)q.Clean.Count;
        if (coverage < 0.7) return false;

        bool artistKnown = q.Artist.Length > 0;
        bool artistOk = false;
        if (artistKnown)
        {
            foreach (var set in SongText.ArtistSets(c.Artist, c.Title))
            {
                bool all = true;
                foreach (var w in set)
                    if (!SongText.ContainsToken(q.Raw, w)) { all = false; break; }
                if (all) { artistOk = true; break; }
            }
            if (!artistOk) return false;
        }

        score = 50 + 30 * coverage;
        if (artistOk) score += 15;
        if (TextMatch.Norm(c.Title) == TextMatch.Norm(q.Title)) score += 10;

        if (q.Seconds > 0 && c.Seconds > 0)
        {
            double d = Math.Abs(c.Seconds - q.Seconds);
            score += d <= 3 ? 10 : d <= 8 ? 5 : d <= 15 ? 0 : d <= 30 ? -10 : -25;
            if (!artistKnown && d > 10) return false;
        }
        else if (!artistKnown && c.Rank > 0)
        {
            return false;
        }

        if (!q.JunkWanted && SongText.Junk.IsMatch(c.Title + " " + c.Album)) score -= 50;
        score -= c.Rank * 0.5;
        return score >= Accept;
    }
}

/// <summary>
/// Cross-checks a song online before it is looked up anywhere else. The file may be called "Polo G Flex Official Music Video (HD)" or
/// carry an uploader's name as its artist; the catalogues (Deezer and iTunes, both free and keyless, asked at the same time) know the
/// real artist and title and have the cover. The best matching entry (see <see cref="SongMatcher"/>) gives the song's true identity,
/// which the lyrics and cover look-ups then use. Only the artist, title and length are sent. When nothing matches, or the services
/// can't be reached, the tidied local guess is returned and the look-ups carry on with that.
/// </summary>
internal static class SongResolver
{
    private static readonly HttpClient Http = CreateClient();
    private static readonly ConcurrentDictionary<string, SongIdentity> Cache = new();
    private static long _itunesBlockedUntil;   // iTunes allows only ~20 requests a minute: after a refusal it is left alone for a minute

    private static HttpClient CreateClient()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromSeconds(8) };
        http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("Tempo", "3.0"));
        http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("(desktop media player)"));
        return http;
    }

    /// <summary>False for a minute after iTunes refused a request (too many requests).</summary>
    internal static bool ITunesAvailable => Environment.TickCount64 >= Interlocked.Read(ref _itunesBlockedUntil);

    /// <summary>Who and what the song is: cross-checked online when possible, else the tidied local guess. Never throws (except when cancelled).</summary>
    internal static async Task<SongIdentity> ResolveAsync(string path, TagInfo tags, CancellationToken ct)
    {
        var (artist, title) = SongText.Local(path, tags);
        double seconds = tags.DurationSeconds;
        string album = tags.Album ?? "";
        string cacheKey = path + "|" + seconds.ToString(CultureInfo.InvariantCulture);

        if (Cache.TryGetValue(cacheKey, out var cached)) return cached;

        var local = new SongIdentity(artist, title, album, seconds, Array.Empty<string>(), false, false);
        if (title.Length == 0) return local;

        string raw = string.Join(" ", new[]
        {
            System.Net.WebUtility.HtmlDecode(tags.Title ?? ""),
            tags.Artist ?? "",
            System.Net.WebUtility.HtmlDecode(Path.GetFileNameWithoutExtension(path) ?? "")
        });

        bool answered = false;
        foreach (var (qa, qt) in Variants(path, tags, artist, title))
        {
            ct.ThrowIfCancellationRequested();

            var query = new SongQuery(qa, qt, seconds, raw);
            string term = qa.Length > 0 ? qa + " " + qt : qt;
            var (items, ok) = await SearchCatalogsAsync(term, ct).ConfigureAwait(false);
            answered |= ok;

            var accepted = new List<(SongCandidate Entry, double Score)>();
            foreach (var entry in items)
                if (SongMatcher.TryScore(query, entry, out double s)) accepted.Add((entry, s));
            if (accepted.Count == 0) continue;

            accepted.Sort((x, y) => y.Score.CompareTo(x.Score));
            var best = accepted[0].Entry;

            var covers = new List<string>();
            foreach (var (entry, _) in accepted)
                if (entry.Cover != null && !covers.Contains(entry.Cover) && covers.Count < 4) covers.Add(entry.Cover);

            string trueTitle = TextMatch.StripFeature(best.Title).Trim();
            var identity = new SongIdentity(
                best.Artist.Length > 0 ? best.Artist : qa,
                trueTitle.Length > 0 ? trueTitle : best.Title,
                best.Album.Length > 0 ? best.Album : album,
                best.Seconds > 0 ? best.Seconds : seconds,
                covers, Verified: true, Answered: true);

            if (Cache.Count > 400) Cache.Clear();
            Cache[cacheKey] = identity;
            return identity;
        }

        return local with { Answered = answered };
    }

    /// <summary>
    /// The ways the file might name the song, most likely first: the tidied tags / name; the file name alone; the tag title split at its
    /// dash (when the artist tag is an uploader's name); the title alone. At most four, each asked about once.
    /// </summary>
    private static List<(string Artist, string Title)> Variants(string path, TagInfo tags, string artist, string title)
    {
        var list = new List<(string Artist, string Title)>();

        void Add(string a, string t)
        {
            if (t.Length == 0 || list.Count >= 4) return;
            string key = TextMatch.Norm(a) + "|" + TextMatch.Norm(t);
            foreach (var existing in list)
                if (TextMatch.Norm(existing.Artist) + "|" + TextMatch.Norm(existing.Title) == key) return;
            list.Add((a, t));
        }

        Add(artist, title);

        var fromName = SongText.FromFileName(path);
        Add(TextMatch.StripFeature(fromName.Artist), TextMatch.StripFeature(fromName.Title));

        var fromTag = SongText.SplitArtistTitle(SongText.Polish(DisplayNames.Tidy(tags.Title ?? "")));
        if (fromTag.Artist.Length > 0) Add(SongText.CleanArtist(TextMatch.StripFeature(fromTag.Artist)), TextMatch.StripFeature(fromTag.Title));

        if (artist.Length > 0) Add("", title);
        return list;
    }

    // ---- the catalogues

    private static async Task<(List<SongCandidate> Items, bool Answered)> SearchCatalogsAsync(string term, CancellationToken ct)
    {
        var deezer = SafeAsync(() => DeezerAsync(term, ct), ct);
        var itunes = SafeAsync(() => ITunesAsync(term, ct), ct);
        var results = await Task.WhenAll(deezer, itunes).ConfigureAwait(false);

        var all = new List<SongCandidate>();
        bool answered = false;
        foreach (var r in results)
        {
            all.AddRange(r.Items);
            answered |= r.Ok;
        }
        return (all, answered);
    }

    private static async Task<(List<SongCandidate> Items, bool Ok)> SafeAsync(Func<Task<(List<SongCandidate> Items, bool Ok)>> call, CancellationToken ct)
    {
        try
        {
            return await call().ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            AppLog.Write("Song look-up", ex);   // offline, a timeout or a service that is down: that catalogue just does not answer
            return (new List<SongCandidate>(), false);
        }
    }

    private static string Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    private static double Num(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : 0;

    /// <summary>Deezer's public search: title, artist, length and the album's cover, no key needed.</summary>
    private static async Task<(List<SongCandidate> Items, bool Ok)> DeezerAsync(string term, CancellationToken ct)
    {
        var items = new List<SongCandidate>();
        using var response = await Http.GetAsync("https://api.deezer.com/search?limit=10&q=" + Uri.EscapeDataString(term), ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode) return (items, false);

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object || root.TryGetProperty("error", out _)) return (items, false);   // e.g. "quota limit exceeded"
        if (!root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array) return (items, true);

        int rank = 0;
        foreach (var e in data.EnumerateArray())
        {
            string title = Str(e, "title");
            string artist = e.TryGetProperty("artist", out var a) ? Str(a, "name") : "";
            string album = "", cover = "";
            if (e.TryGetProperty("album", out var al) && al.ValueKind == JsonValueKind.Object)
            {
                album = Str(al, "title");
                cover = Str(al, "cover_big");
                if (cover.Length == 0) cover = Str(al, "cover_xl");
                if (cover.Contains("/cover//")) cover = "";   // an album with no picture of its own
            }
            items.Add(new SongCandidate(title, artist, album, Num(e, "duration"), cover.Length == 0 ? null : cover, rank++));
        }
        return (items, true);
    }

    /// <summary>The iTunes Search API: title, artist, length and artwork, no key needed.</summary>
    private static async Task<(List<SongCandidate> Items, bool Ok)> ITunesAsync(string term, CancellationToken ct)
    {
        var items = new List<SongCandidate>();
        if (!ITunesAvailable) return (items, false);

        using var response = await Http.GetAsync("https://itunes.apple.com/search?media=music&entity=song&limit=10&term=" + Uri.EscapeDataString(term), ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            int code = (int)response.StatusCode;
            if (code == 403 || code == 429 || code == 503) Interlocked.Exchange(ref _itunesBlockedUntil, Environment.TickCount64 + 60_000);
            return (items, false);
        }

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
        if (!doc.RootElement.TryGetProperty("results", out var results) || results.ValueKind != JsonValueKind.Array) return (items, true);

        int rank = 0;
        foreach (var r in results.EnumerateArray())
        {
            string art = Str(r, "artworkUrl100");
            string cover = art.Length == 0 ? "" : art.Replace("100x100bb", "600x600bb").Replace("100x100-", "600x600-");
            items.Add(new SongCandidate(Str(r, "trackName"), Str(r, "artistName"), Str(r, "collectionName"),
                Num(r, "trackTimeMillis") / 1000.0, cover.Length == 0 ? null : cover, rank++));
        }
        return (items, true);
    }
}

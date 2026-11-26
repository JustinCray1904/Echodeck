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
        http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("Tempo", "1.0"));
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
    internal static List<(string Artist, string Title)> Variants(string path, TagInfo tags, string artist, string title)
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

        // The title alone (for a file whose artist is an uploader's name) accepts ANY song with that title and about that length, so it
        // is only tried when the file itself does not say who made the song. A file called "Juice WRLD - Girlfriend" has an artist: asking
        // for just "Girlfriend" used to bring back a compilation's "Girlfriend" by somebody else, with its cover.
        if (artist.Length > 0 && fromName.Artist.Length == 0) Add("", title);
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

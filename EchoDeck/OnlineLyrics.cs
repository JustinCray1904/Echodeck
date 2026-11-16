using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace EchoDeck;

/// <summary>
/// Looks lyrics up at lrclib.net: a free, open lyrics database that needs no account or key and has timed (.lrc) lyrics for
/// many songs. Only the title, artist, album and length of the song are sent; the audio never leaves the PC.
/// </summary>
internal static class OnlineLyrics
{
    /// <param name="Approximate">Matched by title only: no upload with a length close to the file's. The timing may be off.</param>
    internal sealed record Result(string Text, bool Synced, string Title, string Artist, bool Approximate = false);

    private sealed record Hit(string Title, string Artist, double Duration, string Synced, string Plain, bool Instrumental);

    private static readonly HttpClient Http = CreateClient();

    private static HttpClient CreateClient()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("Tempo", "3.0"));
        return http;
    }

    /// <summary>Best guess of artist and title from a file name such as "yt1z.net - Juice WRLD - Ten Dirty (Official Video) [320 KBps]".</summary>
    internal static (string Artist, string Title) FromFileName(string path) => SongText.FromFileName(path);

    /// <summary>
    /// Who and what a song is from the file alone, for every online lookup (lyrics, covers, Discord): the tags when they are good, else
    /// the tidied file name (the same name the library shows). "(feat. ...)" is left out of the title, because databases list the
    /// featured artist separately. FindAsync and the cover search also cross-check this online (SongResolver).
    /// </summary>
    internal static (string Artist, string Title) Identity(string path, TagInfo tags) => SongText.Local(path, tags);

    /// <summary>The upload is not the same recording when it is this much longer or shorter than the file (seconds).</summary>
    private const double MaxLengthDifference = 20;

    /// <summary>The length difference above which the timing is flagged as possibly off (seconds).</summary>
    private const double ExactEnough = 6;

    /// <summary>
    /// Of all the candidates, the one that is this song: the title must match (and the artist, when it is known), and of those the one
    /// whose length is CLOSEST to the file's wins (timed lyrics before plain ones only when the lengths are within a couple of seconds of
    /// each other). A result for a different song is never taken just because its length happens to be close, and a different artist
    /// is only forgiven for an upload of exactly the file's length and a title that is not a common short word.
    /// </summary>
    private static Hit? Pick(IEnumerable<Hit> hits, IReadOnlyList<string> titles, IReadOnlyList<string> artists, double seconds, out double gap)
    {
        gap = 0;
        Hit? best = null;
        double bestScore = double.MaxValue;

        bool artistKnown = artists.Any(a => a.Length > 0);
        int titleLength = titles.Count > 0 ? TextMatch.Norm(titles[0]).Length : 0;
        double limit = titleLength <= 4 ? 8 : MaxLengthDifference;   // short titles ("Ye", "Home") are easily confused: be stricter

        foreach (var h in hits)
        {
            if (h.Instrumental || (h.Synced.Length == 0 && h.Plain.Length == 0)) continue;
            if (!titles.Any(t => TextMatch.SameTitle(t, h.Title))) continue;

            bool haveLength = seconds > 0 && h.Duration > 0;
            double diff = haveLength ? Math.Abs(h.Duration - seconds) : 0;
            if (diff > limit) continue;

            bool artistOk = !artistKnown || artists.Any(a => a.Length > 0 && TextMatch.SameArtist(a, h.Artist));
            if (!artistOk && !(haveLength && diff <= 2 && titleLength >= 8)) continue;
            if (!artistKnown && haveLength && diff > 5) continue;   // no artist to check: the length has to vouch for the song

            double score = diff + (h.Synced.Length > 0 ? 0 : 2.5) + (artistOk ? 0 : 3);
            if (score < bestScore)
            {
                best = h;
                bestScore = score;
                gap = diff;
            }
        }
        return best;
    }

    /// <summary>Null when nothing was found. Throws on network trouble (the caller shows a message).</summary>
    internal static async Task<Result?> FindAsync(string mediaPath, TagInfo tags, CancellationToken ct)
    {
        var local = Identity(mediaPath, tags);
        double seconds = tags.DurationSeconds;
        if (local.Title.Length == 0) return null;

        // Cross-check the song online first (the real artist and title, whatever the file is called). If that does not work out
        // (offline, not in the catalogues) the tidied local guess is used, so this step can only help.
        SongIdentity id;
        try { id = await SongResolver.ResolveAsync(mediaPath, tags, ct).ConfigureAwait(false); }
        catch (OperationCanceledException) { throw; }
        catch { id = new SongIdentity(local.Artist, local.Title, tags.Album ?? "", seconds, Array.Empty<string>(), false, false); }

        string artist = id.Verified ? id.Artist : local.Artist;
        string title = id.Verified ? id.Title : local.Title;
        string album = !string.IsNullOrWhiteSpace(tags.Album) ? tags.Album : id.Album;

        // what a hit may be called: the verified name only (the title is known now), else the local guess
        var titles = new List<string> { title };
        if (id.Verified && !titles.Contains(id.TitleBare)) titles.Add(id.TitleBare);
        var artists = new List<string> { artist };
        if (id.Verified && local.Artist.Length > 0 && !artists.Contains(local.Artist)) artists.Add(local.Artist);

        var all = new List<Hit>();
        string Num(double v) => ((long)v).ToString(System.Globalization.CultureInfo.InvariantCulture);

        // 1) the exact lookup (needs the artist): the database itself matches title, artist and length
        if (artist.Length > 0)
        {
            var url = "https://lrclib.net/api/get?track_name=" + Uri.EscapeDataString(title) +
                      "&artist_name=" + Uri.EscapeDataString(artist) +
                      (album.Length > 0 ? "&album_name=" + Uri.EscapeDataString(album) : "") +
                      (seconds > 0 ? "&duration=" + Num(seconds) : "");
            JsonElement? body = null;
            try { body = await GetAsync(url, ct).ConfigureAwait(false); }
            catch (RateLimitedException) { throw; }
            catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.BadRequest) { /* a missing detail: the searches below do not need it */ }
            if (body != null && ParseHit(body.Value) is { } exact) all.Add(exact);

            var best = Pick(all, titles, artists, seconds, out double gap);
            if (best != null && best.Synced.Length > 0 && gap <= ExactEnough) return ToResult(best);
        }

        // 2) searches, most specific first; each is checked at once, and the next one is only made when this one was not good enough
        var searches = new List<string>();
        void AddSearch(string q) { if (!searches.Contains(q)) searches.Add(q); }
        if (artist.Length > 0) AddSearch("track_name=" + Uri.EscapeDataString(title) + "&artist_name=" + Uri.EscapeDataString(artist));
        if (artist.Length > 0) AddSearch("q=" + Uri.EscapeDataString(artist + " " + title));
        if (id.Verified && local.Artist.Length > 0 && local.Title.Length > 0)
            AddSearch("q=" + Uri.EscapeDataString(local.Artist + " " + local.Title));
        AddSearch("track_name=" + Uri.EscapeDataString(title));
        AddSearch("q=" + Uri.EscapeDataString(title));

        foreach (var search in searches)
        {
            ct.ThrowIfCancellationRequested();
            var body = await GetAsync("https://lrclib.net/api/search?" + search, ct).ConfigureAwait(false);
            if (body == null || body.Value.ValueKind != JsonValueKind.Array) continue;

            foreach (var element in body.Value.EnumerateArray())
                if (ParseHit(element) is { } hit) all.Add(hit);

            var best = Pick(all, titles, artists, seconds, out double gap);
            if (best != null && best.Synced.Length > 0 && gap <= ExactEnough) return ToResult(best);
        }

        var pick = Pick(all, titles, artists, seconds, out double finalGap);
        return pick != null ? ToResult(pick, approximate: seconds > 0 && finalGap > ExactEnough) : null;
    }

    private static Result ToResult(Hit hit, bool approximate = false) =>
        hit.Synced.Length > 0
            ? new Result(hit.Synced.Replace("\r\n", "\n").Trim() + "\n", true, hit.Title, hit.Artist, approximate)
            : new Result(hit.Plain.Replace("\r\n", "\n").Trim() + "\n", false, hit.Title, hit.Artist, approximate);

    /// <summary>The JSON body, or null for "not found" (404).</summary>
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static long _lastRequest;

    /// <summary>Thrown when the service keeps answering "too many requests" even after waiting.</summary>
    internal sealed class RateLimitedException : HttpRequestException
    {
        public RateLimitedException() : base("the lyrics service asks to slow down (too many requests)") { }
    }

    private static async Task<JsonElement?> GetAsync(string url, CancellationToken ct)
    {
        for (int attempt = 0; ; attempt++)
        {
            // never more than about one request a second, whatever is asking (a batch of hundreds of songs gets "429 Too Many Requests" otherwise)
            await Gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                long wait = 900 - (Environment.TickCount64 - _lastRequest);
                if (wait > 0) await Task.Delay((int)wait, ct).ConfigureAwait(false);
                _lastRequest = Environment.TickCount64;
            }
            finally { Gate.Release(); }

            using var response = await Http.GetAsync(url, ct).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.NotFound) return null;

            if ((int)response.StatusCode == 429)
            {
                if (attempt >= 3) throw new RateLimitedException();
                double seconds = response.Headers.RetryAfter?.Delta?.TotalSeconds ?? 6 * (attempt + 1);
                await Task.Delay(TimeSpan.FromSeconds(Math.Clamp(seconds, 3, 45)), ct).ConfigureAwait(false);   // do as asked: wait, then try again
                continue;
            }

            response.EnsureSuccessStatusCode();
            var json = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.Clone();   // the document is disposed here: keep a copy
        }
    }

    private static Hit? ParseHit(JsonElement e)
    {
        if (e.ValueKind != JsonValueKind.Object) return null;

        static string Text(JsonElement o, string name) =>
            o.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? (v.GetString() ?? "") : "";

        double duration = e.TryGetProperty("duration", out var d) && d.ValueKind == JsonValueKind.Number ? d.GetDouble() : 0;
        bool instrumental = e.TryGetProperty("instrumental", out var i) && i.ValueKind == JsonValueKind.True;
        return new Hit(Text(e, "trackName"), Text(e, "artistName"), duration, Text(e, "syncedLyrics").Trim(), Text(e, "plainLyrics").Trim(), instrumental);
    }
}

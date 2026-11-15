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

    private static readonly Regex SitePrefix = new(@"^\s*(?:[\w-]+\.)+(?:net|com|app|xyz|org|io|cc|to|me|site|co)\s*[-–—_]\s*", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex Noise = new(@"[\(\[][^\)\]]*(?:official|video|audio|lyric|visuali[sz]er|prod\.?|kbps|\d{3,4}p|\bhd\b|\bhq\b|remaster|clip)[^\)\]]*[\)\]]", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>Best guess of artist and title from a file name such as "yt1z.net - Juice WRLD - Ten Dirty (Official Video) [320 KBps]".</summary>
    internal static (string Artist, string Title) FromFileName(string path)
    {
        var name = Path.GetFileNameWithoutExtension(path) ?? "";
        name = SitePrefix.Replace(name, "");
        name = Noise.Replace(name, "");
        name = Regex.Replace(name, @"\s+", " ").Trim(' ', '-', '_', '.');

        var parts = name.Split(new[] { " - ", " – ", " — " }, 2, StringSplitOptions.None);
        return parts.Length == 2 && parts[0].Trim().Length > 0 && parts[1].Trim().Length > 0
            ? (parts[0].Trim(), parts[1].Trim())
            : ("", name);
    }

    /// <summary>Null when nothing was found. Throws on network trouble (the caller shows a message).</summary>
    internal static async Task<Result?> FindAsync(string mediaPath, TagInfo tags, CancellationToken ct)
    {
        var fromName = FromFileName(mediaPath);
        string artist = tags.Artist.Length > 0 ? tags.Artist : fromName.Artist;
        string title = tags.Title.Length > 0 ? tags.Title : fromName.Title;
        string album = tags.Album;
        double seconds = tags.DurationSeconds;
        if (title.Length == 0) return null;

        Hit? plainFallback = null;
        var allHits = new List<Hit>();   // every usable search result, for the title-only second chance

        // 1) the exact lookup: needs artist and title, and is the most reliable when the tags are good
        if (artist.Length > 0)
        {
            var url = "https://lrclib.net/api/get?track_name=" + Uri.EscapeDataString(title) +
                      "&artist_name=" + Uri.EscapeDataString(artist) +
                      (album.Length > 0 ? "&album_name=" + Uri.EscapeDataString(album) : "") +
                      (seconds > 0 ? "&duration=" + ((long)seconds).ToString(System.Globalization.CultureInfo.InvariantCulture) : "");
            var body = await GetAsync(url, ct).ConfigureAwait(false);
            if (body != null)
            {
                var hit = ParseHit(body.Value);
                if (hit != null && !hit.Instrumental)
                {
                    if (hit.Synced.Length > 0) return ToResult(hit);
                    if (hit.Plain.Length > 0) plainFallback = hit;
                }
            }
        }

        // 2) a search: by artist and title, then by the file name's title alone
        var queries = new List<string>();
        if (artist.Length > 0) queries.Add(artist + " " + title);
        queries.Add(title);
        if (fromName.Title.Length > 0 && !queries.Contains(fromName.Title)) queries.Add(fromName.Title);

        foreach (var query in queries)
        {
            ct.ThrowIfCancellationRequested();
            var body = await GetAsync("https://lrclib.net/api/search?q=" + Uri.EscapeDataString(query), ct).ConfigureAwait(false);
            if (body == null || body.Value.ValueKind != JsonValueKind.Array) continue;

            var hits = new List<Hit>();
            foreach (var element in body.Value.EnumerateArray())
            {
                var hit = ParseHit(element);
                if (hit != null && !hit.Instrumental && (hit.Synced.Length > 0 || hit.Plain.Length > 0)) hits.Add(hit);
            }
            if (hits.Count == 0) continue;
            allHits.AddRange(hits);

            // the same length as the file (within a few seconds) is what makes it the same recording
            Func<Hit, double> distance = h => seconds > 0 && h.Duration > 0 ? Math.Abs(h.Duration - seconds) : 0;
            var closeEnough = hits.Where(h => seconds <= 0 || h.Duration <= 0 || distance(h) <= 6).ToList();

            var synced = closeEnough.Where(h => h.Synced.Length > 0).OrderBy(distance).FirstOrDefault();
            if (synced != null) return ToResult(synced);

            plainFallback ??= closeEnough.Where(h => h.Plain.Length > 0).OrderBy(distance).FirstOrDefault();
        }

        if (plainFallback != null) return ToResult(plainFallback);

        // nothing has a length close to the file's: take the best match by title (and artist), closest length first, timed before plain
        string wantTitle = Norm(title), wantArtist = Norm(artist);
        Func<Hit, double> gap = h => seconds > 0 && h.Duration > 0 ? Math.Abs(h.Duration - seconds) : 9999;
        var byTitle = allHits
            .Where(h => Norm(h.Title).Length > 0 && (Norm(h.Title) == wantTitle ||
                        (wantTitle.Length >= 4 && (Norm(h.Title).Contains(wantTitle) || wantTitle.Contains(Norm(h.Title))))))
            .OrderByDescending(h => wantArtist.Length > 0 && (Norm(h.Artist).Contains(wantArtist) || wantArtist.Contains(Norm(h.Artist))))
            .ThenByDescending(h => h.Synced.Length > 0)
            .ThenBy(gap)
            .FirstOrDefault();
        return byTitle != null ? ToResult(byTitle, approximate: true) : null;
    }

    /// <summary>Letters and digits only, lower case, without bracketed extras: "Flex (Official Audio)" and "flex" compare equal.</summary>
    private static string Norm(string s) =>
        Regex.Replace(Regex.Replace(s ?? "", @"[\(\[][^\)\]]*[\)\]]", ""), @"[^\p{L}\p{Nd}]", "").ToLowerInvariant();

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

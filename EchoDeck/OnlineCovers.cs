using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Windows.Media.Imaging;

namespace EchoDeck;

/// <summary>
/// Finds a cover for a song. The song is first cross-checked online (SongResolver: Deezer and iTunes asked at the same time) so the
/// real artist and title are known even when the file is called "Polo G Flex Official Music Video"; the cover that came with the
/// matching entry is used. If the catalogues have no entry, the song's album is tried (iTunes), then MusicBrainz with the Cover Art
/// Archive. Only the artist, title, album and length are sent. Covers are kept in %LOCALAPPDATA%\Tempo\Covers (with the picture's web
/// address, which Discord needs). A song with no cover to be found is remembered for 2 days, but only when a catalogue really
/// answered: being offline or a busy service is never remembered. Off unless the setting is on.
/// </summary>
internal static class OnlineCovers
{
    private static readonly HttpClient Http = CreateClient();
    private static readonly SemaphoreSlim OneAtATime = new(2, 2);

    // Negative results of older builds had no marker: they are searched again with the smarter look-up.
    private const string NoneMarker = "v2";
    private static readonly TimeSpan NoneFor = TimeSpan.FromDays(2);

    private static bool RecentlyNone(string noneFile)
    {
        try
        {
            if (!File.Exists(noneFile) || DateTime.UtcNow - File.GetLastWriteTimeUtc(noneFile) >= NoneFor) return false;
            return File.ReadAllText(noneFile).Trim() == NoneMarker;
        }
        catch { return false; }
    }

    private static HttpClient CreateClient()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromSeconds(8) };
        http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("Tempo", "3.0"));
        http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("(desktop media player)"));
        return http;
    }

    private static string Dir => Path.Combine(AppPaths.DataDir, "Covers");

    private static string KeyFor(string path)
    {
        string full;
        try { full = Path.GetFullPath(path); } catch { full = path; }
        var hash = System.Security.Cryptography.SHA1.HashData(System.Text.Encoding.UTF8.GetBytes(full.ToLowerInvariant()));
        return Convert.ToHexString(hash, 0, 8).ToLowerInvariant();
    }

    // ---- covers the user says are wrong, and covers saved by an earlier build

    /// <summary>
    /// Earlier builds took MusicBrainz covers without checking WHO made the recording (a "Girlfriend" by another artist gave its
    /// cover). Covers saved from there by those builds carry no ".v3" marker and are dropped once, so they are looked up again with the
    /// artist check. Covers from the catalogues and everything saved by this build are kept.
    /// </summary>
    private static void PurgeLegacyMusicBrainzCover(string key)
    {
        try
        {
            string urlFile = Path.Combine(Dir, key + ".url");
            if (File.Exists(Path.Combine(Dir, key + ".v3")) || !File.Exists(urlFile)) return;
            if (!File.ReadAllText(urlFile).Contains("coverartarchive.org", StringComparison.OrdinalIgnoreCase)) return;
            foreach (var ext in new[] { ".jpg", ".url" })
                try { File.Delete(Path.Combine(Dir, key + ext)); } catch { /* in use: next time */ }
        }
        catch { /* best effort */ }
    }

    /// <summary>
    /// "This cover is wrong": the online cover is forgotten, its web address is remembered as bad (it is never picked again for this
    /// file), and the picture inside the file is no longer used for it either, so the next look-up takes the next best match.
    /// </summary>
    public static void Reject(string path)
    {
        try
        {
            string key = KeyFor(path);
            Directory.CreateDirectory(Dir);
            string urlFile = Path.Combine(Dir, key + ".url");
            if (File.Exists(urlFile)) File.AppendAllText(Path.Combine(Dir, key + ".bad"), File.ReadAllText(urlFile).Trim() + "\n");
            foreach (var ext in new[] { ".jpg", ".url", ".none", ".v3" })
                try { File.Delete(Path.Combine(Dir, key + ext)); } catch { /* ignore */ }
            File.WriteAllText(Path.Combine(Dir, key + ".skip"), "1");
        }
        catch (Exception ex) { AppLog.Write("Reject cover", ex); }
    }

    /// <summary>True once the user rejected this file's cover: the picture inside the file is then not used.</summary>
    public static bool SkipsEmbedded(string path)
    {
        try { return File.Exists(Path.Combine(Dir, KeyFor(path) + ".skip")); }
        catch { return false; }
    }

    private static bool IsBad(string key, string url)
    {
        try
        {
            string bad = Path.Combine(Dir, key + ".bad");
            return File.Exists(bad) && File.ReadAllLines(bad).Any(l => string.Equals(l.Trim(), url, StringComparison.OrdinalIgnoreCase));
        }
        catch { return false; }
    }

    private static Found? WithoutBad(Found? found, string key)
    {
        if (found == null) return null;
        if (found.Bytes != null) return found.Urls.Count > 0 && IsBad(key, found.Urls[0]) ? null : found;
        var kept = found.Urls.Where(u => !IsBad(key, u)).ToList();
        return kept.Count == 0 ? null : new Found(kept, null);
    }

    private sealed record Found(IReadOnlyList<string> Urls, byte[]? Bytes);

    /// <summary>The cover as a frozen picture, or null (none found, offline, or nothing to search with).</summary>
    public static async Task<BitmapSource?> GetAsync(string path)
    {
        try
        {
            string key = KeyFor(path);
            string file = Path.Combine(Dir, key + ".jpg");
            string none = Path.Combine(Dir, key + ".none");

            PurgeLegacyMusicBrainzCover(key);
            if (File.Exists(file)) return Decode(await File.ReadAllBytesAsync(file).ConfigureAwait(false));
            if (RecentlyNone(none)) return null;

            var bytes = await FindAndSaveAsync(path, key).ConfigureAwait(false);
            return bytes == null ? null : Decode(bytes);
        }
        catch (Exception ex)
        {
            AppLog.Write("Online cover", ex);   // offline or the service is busy: just no cover this time (nothing is remembered)
            return null;
        }
    }

    /// <summary>The cover's web address (for Discord, which can only show pictures that are online), or null.</summary>
    public static async Task<string?> GetUrlAsync(string path)
    {
        try
        {
            string key = KeyFor(path);
            string urlFile = Path.Combine(Dir, key + ".url");
            if (File.Exists(urlFile)) return (await File.ReadAllTextAsync(urlFile).ConfigureAwait(false)).Trim();
            if (RecentlyNone(Path.Combine(Dir, key + ".none"))) return null;

            await FindAndSaveAsync(path, key).ConfigureAwait(false);
            return File.Exists(urlFile) ? (await File.ReadAllTextAsync(urlFile).ConfigureAwait(false)).Trim() : null;
        }
        catch (Exception ex)
        {
            AppLog.Write("Online cover address", ex);
            return null;
        }
    }

    private static async Task<byte[]?> FindAndSaveAsync(string path, string key)
    {
        await OneAtATime.WaitAsync().ConfigureAwait(false);
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            Directory.CreateDirectory(Dir);

            var (found, answered) = await SearchAsync(path, cts.Token).ConfigureAwait(false);
            if (found == null)
            {
                // only a real "there is none" is remembered; offline or a busy service just means no cover this time
                if (answered) File.WriteAllText(Path.Combine(Dir, key + ".none"), NoneMarker);
                return null;
            }

            byte[]? bytes = found.Bytes;
            string used = found.Urls.Count > 0 ? found.Urls[0] : "";
            if (bytes == null)
            {
                foreach (var url in found.Urls)
                {
                    try
                    {
                        var data = await Http.GetByteArrayAsync(url, cts.Token).ConfigureAwait(false);
                        if (data.Length >= 1500) { bytes = data; used = url; break; }
                    }
                    catch (HttpRequestException) { /* this picture is gone: try the next one */ }
                }
            }
            if (bytes == null) return null;

            File.WriteAllBytes(Path.Combine(Dir, key + ".jpg"), bytes);
            File.WriteAllText(Path.Combine(Dir, key + ".url"), used);
            File.WriteAllText(Path.Combine(Dir, key + ".v3"), "1");   // saved by a build that checks the artist
            try { File.Delete(Path.Combine(Dir, key + ".none")); } catch { /* ignore */ }
            return bytes;
        }
        finally
        {
            OneAtATime.Release();
        }
    }

    private static BitmapSource? Decode(byte[] bytes)
    {
        if (bytes.Length < 500) return null;
        using var stream = new MemoryStream(bytes);
        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.StreamSource = stream;
        image.EndInit();
        image.Freeze();
        return image;
    }

    private static async Task<(Found? Found, bool Answered)> SearchAsync(string path, CancellationToken ct)
    {
        var tags = LibraryScanner.ReadTags(path);
        string coverKey = KeyFor(path);

        // 1) cross-check the song online; the cover that came with the matching entry is the one
        var id = await SongResolver.ResolveAsync(path, tags, ct).ConfigureAwait(false);
        if (id.Title.Length == 0) return (null, false);   // nothing to search with: not remembered

        // An unofficial remix / slowed / sped-up version: the catalogues only know the original, whose cover is not this version's.
        // Its own YouTube thumbnail is tried first; the original's cover stays as the fallback.
        bool useYouTube = AppSettings.Current.CoverFromYouTube;
        string rawName = (tags.Title ?? "") + " " + Path.GetFileNameWithoutExtension(path);
        bool unofficialVersion = id.Verified && SongText.Junk.IsMatch(rawName) && !SongText.Junk.IsMatch(id.Title);
        if (useYouTube && unofficialVersion)
        {
            var youtube = WithoutBad(await FromYouTubeAsync(path, tags, ct).ConfigureAwait(false), coverKey);
            if (youtube != null) return (youtube, true);
        }
        var fromCatalogue = WithoutBad(id.CoverUrls.Count > 0 ? new Found(id.CoverUrls, null) : null, coverKey);
        if (fromCatalogue != null) return (fromCatalogue, true);

        // 2) the song is not in the catalogues (or has no picture there), but its album may be
        string album = (tags.Album ?? "").Trim();
        if (id.Artist.Length > 0 && album.Length > 0 && SongResolver.ITunesAvailable)
        {
            var fromAlbum = WithoutBad(await FromITunesAlbumAsync(id.Artist, album, ct).ConfigureAwait(false), coverKey);
            if (fromAlbum != null) return (fromAlbum, true);
        }

        // 3) MusicBrainz + Cover Art Archive, with the true artist and title when they were found
        if (id.Artist.Length > 0)
        {
            var fromMb = WithoutBad(await FromMusicBrainzAsync(id.Artist, id.TitleBare, album, ct).ConfigureAwait(false), coverKey);
            if (fromMb != null) return (fromMb, true);
        }

        // 4) the song's YouTube thumbnail
        if (useYouTube && !unofficialVersion)
        {
            var youtube = WithoutBad(await FromYouTubeAsync(path, tags, ct).ConfigureAwait(false), coverKey);
            if (youtube != null) return (youtube, true);
        }
        return (null, id.Answered);
    }

    private static async Task<Found?> FromYouTubeAsync(string path, TagInfo tags, CancellationToken ct)
    {
        try
        {
            string? videoId = YouTubeCovers.IdFromFile(path);
            if (videoId == null)
            {
                var local = SongText.Local(path, tags);
                videoId = await YouTubeCovers.SearchAsync(local.Artist, local.Title, tags.DurationSeconds, ct).ConfigureAwait(false);
            }
            if (videoId == null) return null;

            var thumbnail = await YouTubeCovers.ThumbnailAsync(videoId, ct).ConfigureAwait(false);
            return thumbnail is { } t ? new Found(new[] { t.Url }, t.Bytes) : null;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            AppLog.Write("YouTube cover", ex);   // blocked or changed page: this source just gives nothing
            return null;
        }
    }

    private static string Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    // ---- iTunes: the album's artwork

    private static async Task<Found?> FromITunesAlbumAsync(string artist, string album, CancellationToken ct)
    {
        string url = "https://itunes.apple.com/search?media=music&entity=album&limit=8&term=" + Uri.EscapeDataString(artist + " " + album);
        using var response = await Http.GetAsync(url, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode) return null;

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
        if (!doc.RootElement.TryGetProperty("results", out var results) || results.ValueKind != JsonValueKind.Array) return null;

        foreach (var r in results.EnumerateArray())
        {
            string art = Str(r, "artworkUrl100");
            if (art.Length == 0) continue;
            if (!TextMatch.SameTitle(album, Str(r, "collectionName")) || !TextMatch.SameArtist(artist, Str(r, "artistName"))) continue;
            return new Found(new[] { art.Replace("100x100bb", "600x600bb").Replace("100x100-", "600x600-") }, null);
        }
        return null;
    }

    // ---- MusicBrainz + Cover Art Archive

    /// <summary>Whether a MusicBrainz recording is credited to <paramref name="artist"/> (any of its credited artists).</summary>
    private static bool CreditedTo(JsonElement recording, string artist)
    {
        if (!recording.TryGetProperty("artist-credit", out var credits) || credits.ValueKind != JsonValueKind.Array) return false;
        foreach (var credit in credits.EnumerateArray())
        {
            if (TextMatch.SameArtist(artist, Str(credit, "name"))) return true;
            if (credit.TryGetProperty("artist", out var who) && TextMatch.SameArtist(artist, Str(who, "name"))) return true;
        }
        return false;
    }

    private static async Task<Found?> FromMusicBrainzAsync(string artist, string title, string album, CancellationToken ct)
    {
        static string Plain(string s) => s.Replace('"', ' ').Replace('\\', ' ');
        string query = $"recording:\"{Plain(title)}\" AND artist:\"{Plain(artist)}\"";
        using var response = await Http.GetAsync("https://musicbrainz.org/ws/2/recording/?fmt=json&limit=8&query=" + Uri.EscapeDataString(query), ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode) return null;

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
        if (!doc.RootElement.TryGetProperty("recordings", out var recordings) || recordings.ValueKind != JsonValueKind.Array) return null;

        var ids = new List<(string Id, bool SameAlbum)>();
        foreach (var recording in recordings.EnumerateArray())
        {
            if (!TextMatch.SameTitle(title, Str(recording, "title"))) continue;   // only recordings that really are this song
            if (!CreditedTo(recording, artist)) continue;                          // ...and by this artist (a "Girlfriend" by another act is not it)
            if (!recording.TryGetProperty("releases", out var releases) || releases.ValueKind != JsonValueKind.Array) continue;
            foreach (var release in releases.EnumerateArray())
            {
                string id = Str(release, "id");
                if (id.Length == 0) continue;
                ids.Add((id, album.Length > 0 && string.Equals(Str(release, "title"), album, StringComparison.OrdinalIgnoreCase)));
            }
        }

        foreach (var (id, _) in ids.OrderByDescending(x => x.SameAlbum).Take(4))
        {
            ct.ThrowIfCancellationRequested();
            string url = $"https://coverartarchive.org/release/{id}/front-500";
            using var art = await Http.GetAsync(url, ct).ConfigureAwait(false);
            if (!art.IsSuccessStatusCode) continue;   // 404: this release has no front cover
            var bytes = await art.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
            if (bytes.Length > 1500) return new Found(new[] { url }, bytes);
        }
        return null;
    }
}

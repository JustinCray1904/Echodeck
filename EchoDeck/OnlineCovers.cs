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

    private sealed record Found(IReadOnlyList<string> Urls, byte[]? Bytes);

    /// <summary>The cover as a frozen picture, or null (none found, offline, or nothing to search with).</summary>
    public static async Task<BitmapSource?> GetAsync(string path)
    {
        try
        {
            string key = KeyFor(path);
            string file = Path.Combine(Dir, key + ".jpg");
            string none = Path.Combine(Dir, key + ".none");

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

        // 1) cross-check the song online; the cover that came with the matching entry is the one
        var id = await SongResolver.ResolveAsync(path, tags, ct).ConfigureAwait(false);
        if (id.Title.Length == 0) return (null, false);   // nothing to search with: not remembered
        if (id.CoverUrls.Count > 0) return (new Found(id.CoverUrls, null), true);

        // 2) the song is not in the catalogues (or has no picture there), but its album may be
        string album = (tags.Album ?? "").Trim();
        if (id.Artist.Length > 0 && album.Length > 0 && SongResolver.ITunesAvailable)
        {
            var fromAlbum = await FromITunesAlbumAsync(id.Artist, album, ct).ConfigureAwait(false);
            if (fromAlbum != null) return (fromAlbum, true);
        }

        // 3) MusicBrainz + Cover Art Archive, with the true artist and title when they were found
        if (id.Artist.Length > 0)
        {
            var fromMb = await FromMusicBrainzAsync(id.Artist, id.TitleBare, album, ct).ConfigureAwait(false);
            if (fromMb != null) return (fromMb, true);
        }
        return (null, id.Answered);
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

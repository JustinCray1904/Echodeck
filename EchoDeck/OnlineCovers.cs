using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Windows.Media.Imaging;

namespace EchoDeck;

/// <summary>
/// Finds a cover for a song: the iTunes Search API first (free, no key, and it knows nearly every popular song, in one quick
/// request), then MusicBrainz with the Cover Art Archive (also free) for what iTunes does not have. The song is searched for by
/// its tidy artist and title (the same name the library shows, without "(Official Audio)", "[320 KBps]", "feat. ..." and so on),
/// and a result is only used when its title (and artist) really match. Only the artist, title and length are sent. Covers are kept
/// in %LOCALAPPDATA%\Tempo\Covers (with the picture's web address, which Discord needs), and a song with no cover to be found is
/// remembered for 7 days. Off unless the setting is on.
/// </summary>
internal static class OnlineCovers
{
    private static readonly HttpClient Http = CreateClient();
    private static readonly SemaphoreSlim OneAtATime = new(1, 1);

    private static HttpClient CreateClient()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromSeconds(12) };
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

    private sealed record Found(string Url, byte[]? Bytes);

    /// <summary>The cover as a frozen picture, or null (none found, offline, or nothing to search with).</summary>
    public static async Task<BitmapSource?> GetAsync(string path)
    {
        try
        {
            string key = KeyFor(path);
            string file = Path.Combine(Dir, key + ".jpg");
            string none = Path.Combine(Dir, key + ".none");

            if (File.Exists(file)) return Decode(await File.ReadAllBytesAsync(file).ConfigureAwait(false));
            if (File.Exists(none) && DateTime.UtcNow - File.GetLastWriteTimeUtc(none) < TimeSpan.FromDays(7)) return null;

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
            if (File.Exists(Path.Combine(Dir, key + ".none")) &&
                DateTime.UtcNow - File.GetLastWriteTimeUtc(Path.Combine(Dir, key + ".none")) < TimeSpan.FromDays(7)) return null;

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

            var found = await SearchAsync(path, cts.Token).ConfigureAwait(false);
            if (found == null)
            {
                File.WriteAllBytes(Path.Combine(Dir, key + ".none"), Array.Empty<byte>());
                return null;
            }

            var bytes = found.Bytes ?? await Http.GetByteArrayAsync(found.Url, cts.Token).ConfigureAwait(false);
            if (bytes.Length < 1500)
            {
                File.WriteAllBytes(Path.Combine(Dir, key + ".none"), Array.Empty<byte>());
                return null;
            }
            File.WriteAllBytes(Path.Combine(Dir, key + ".jpg"), bytes);
            File.WriteAllText(Path.Combine(Dir, key + ".url"), found.Url);
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

    private static async Task<Found?> SearchAsync(string path, CancellationToken ct)
    {
        var tags = LibraryScanner.ReadTags(path);
        var (artist, title) = OnlineLyrics.Identity(path, tags);
        if (title.Length == 0) return null;

        return await FromITunesAsync(artist, title, tags.DurationSeconds, ct).ConfigureAwait(false)
            ?? (artist.Length > 0 ? await FromMusicBrainzAsync(artist, title, tags.Album ?? "", ct).ConfigureAwait(false) : null);
    }

    // ---- iTunes Search API

    private static async Task<Found?> FromITunesAsync(string artist, string title, double seconds, CancellationToken ct)
    {
        // the artist and title together when the artist is known, else the title alone
        string term = artist.Length > 0 ? artist + " " + title : title;
        string url = "https://itunes.apple.com/search?media=music&entity=song&limit=15&term=" + Uri.EscapeDataString(term);

        using var response = await Http.GetAsync(url, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode) return null;
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
        if (!doc.RootElement.TryGetProperty("results", out var results) || results.ValueKind != JsonValueKind.Array) return null;

        string? bestUrl = null;
        double bestScore = double.MaxValue;
        int order = 0;
        foreach (var r in results.EnumerateArray())
        {
            order++;
            string name = Str(r, "trackName"), by = Str(r, "artistName"), art = Str(r, "artworkUrl100");
            if (art.Length == 0 || !TextMatch.SameTitle(title, name)) continue;
            if (artist.Length > 0 && !TextMatch.SameArtist(artist, by)) continue;

            // the song's length is the tie-break between versions (single, album, live); without it the first (most relevant) result
            double diff = 0;
            if (seconds > 0 && r.TryGetProperty("trackTimeMillis", out var ms) && ms.ValueKind == JsonValueKind.Number)
                diff = Math.Abs(ms.GetDouble() / 1000.0 - seconds);
            if (seconds > 0 && diff > 25) continue;   // a different recording

            double score = diff + order * 0.01;
            if (score < bestScore)
            {
                bestScore = score;
                bestUrl = art.Replace("100x100bb", "600x600bb").Replace("100x100-", "600x600-");
            }
        }
        return bestUrl == null ? null : new Found(bestUrl, null);
    }

    private static string Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

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

        foreach (var (id, _) in ids.OrderByDescending(x => x.SameAlbum).Take(6))
        {
            ct.ThrowIfCancellationRequested();
            string url = $"https://coverartarchive.org/release/{id}/front-500";
            using var art = await Http.GetAsync(url, ct).ConfigureAwait(false);
            if (!art.IsSuccessStatusCode) continue;   // 404: this release has no front cover
            var bytes = await art.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
            if (bytes.Length > 1500) return new Found(url, bytes);
        }
        return null;
    }
}

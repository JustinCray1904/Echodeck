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
/// remembered for one day. Off unless the setting is on.
/// </summary>
internal static class OnlineCovers
{
    private static readonly HttpClient Http = CreateClient();
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, SemaphoreSlim> PerTrackLocks = new(StringComparer.OrdinalIgnoreCase);
    private static readonly SemaphoreSlim MusicBrainzGate = new(1, 1);
    private static long _lastMusicBrainzRequest;

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
        string identity = full.ToLowerInvariant();
        try
        {
            var info = new FileInfo(full);
            if (info.Exists) identity += "|" + info.Length + "|" + info.LastWriteTimeUtc.Ticks;
        }
        catch { }
        var hash = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(identity));
        return Convert.ToHexString(hash, 0, 12).ToLowerInvariant();
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
            if (File.Exists(none) && DateTime.UtcNow - File.GetLastWriteTimeUtc(none) < TimeSpan.FromDays(1)) return null;

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
                DateTime.UtcNow - File.GetLastWriteTimeUtc(Path.Combine(Dir, key + ".none")) < TimeSpan.FromDays(1)) return null;

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
        var gate = PerTrackLocks.GetOrAdd(key, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync().ConfigureAwait(false);
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            Directory.CreateDirectory(Dir);
            string cachedImage = Path.Combine(Dir, key + ".jpg");
            string cachedNone = Path.Combine(Dir, key + ".none");
            if (File.Exists(cachedImage)) return await File.ReadAllBytesAsync(cachedImage, cts.Token).ConfigureAwait(false);
            if (File.Exists(cachedNone) && DateTime.UtcNow - File.GetLastWriteTimeUtc(cachedNone) < TimeSpan.FromDays(1)) return null;

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
            gate.Release();
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

        // Resolve the cleaned artist/title against a music catalog first. Its canonical metadata also improves
        // the second-stage Cover Art Archive lookup. Catalog failure is non-fatal: use the local identity.
        var catalog = await OnlineTrackIdentity.ResolveAsync(artist, title, tags.DurationSeconds, ct).ConfigureAwait(false);
        if (catalog != null)
        {
            artist = catalog.Artist;
            title = catalog.Title;
            if (!string.IsNullOrWhiteSpace(catalog.ArtworkUrl))
                return new Found(catalog.ArtworkUrl.Replace("100x100bb", "600x600bb").Replace("100x100-", "600x600-"), null);
        }

        return artist.Length > 0
            ? await FromMusicBrainzAsync(artist, title, tags.Album ?? "", tags.DurationSeconds, ct).ConfigureAwait(false)
            : null;
    }

    private static string Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    // ---- MusicBrainz + Cover Art Archive

    private static async Task<Found?> FromMusicBrainzAsync(string artist, string title, string album, double durationSeconds, CancellationToken ct)
    {
        static string Plain(string s) => s.Replace('"', ' ').Replace('\\', ' ');
        string query = $"recording:\"{Plain(title)}\" AND artist:\"{Plain(artist)}\"";
        HttpResponseMessage response;
        await MusicBrainzGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            long wait = 1100 - (Environment.TickCount64 - _lastMusicBrainzRequest);
            if (wait > 0) await Task.Delay((int)wait, ct).ConfigureAwait(false);
            _lastMusicBrainzRequest = Environment.TickCount64;
            response = await Http.GetAsync("https://musicbrainz.org/ws/2/recording/?fmt=json&limit=8&query=" + Uri.EscapeDataString(query), ct).ConfigureAwait(false);
        }
        finally { MusicBrainzGate.Release(); }
        using (response)
        {
            if (!response.IsSuccessStatusCode) return null;
            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
            if (!doc.RootElement.TryGetProperty("recordings", out var recordings) || recordings.ValueKind != JsonValueKind.Array) return null;

            var ids = new List<(string Id, bool SameAlbum)>();
        foreach (var recording in recordings.EnumerateArray())
        {
            if (TextMatch.Norm(title).Length == 0 || TextMatch.Norm(title) != TextMatch.Norm(Str(recording, "title"))) continue;
            string credit = "";
            if (recording.TryGetProperty("artist-credit", out var credits) && credits.ValueKind == JsonValueKind.Array)
                credit = string.Join(" ", credits.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.Object)
                    .Select(x => x.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String ? n.GetString() : "")
                    .Where(x => !string.IsNullOrWhiteSpace(x)));
            if (artist.Length > 0 && !TextMatch.SameArtist(artist, credit)) continue;
            if (durationSeconds > 0 && recording.TryGetProperty("length", out var length) && length.ValueKind == JsonValueKind.Number)
            {
                double diff = Math.Abs(length.GetDouble() / 1000.0 - durationSeconds);
                if (diff > 15) continue;
            }
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
}

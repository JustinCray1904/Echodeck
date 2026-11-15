using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Windows.Media.Imaging;

namespace EchoDeck;

/// <summary>
/// Finds a cover for a song that has none: MusicBrainz (free, no key) is asked which releases contain the song, and the Cover Art
/// Archive (also free) supplies the picture. Only the artist and title are sent. Covers are kept in %LOCALAPPDATA%\Tempo\Covers, and a
/// song with no cover to be found is remembered for 30 days so it is not asked again every time. Off unless the setting is on.
/// </summary>
internal static class OnlineCovers
{
    private static readonly HttpClient Http = CreateClient();
    private static readonly SemaphoreSlim OneAtATime = new(1, 1);   // MusicBrainz asks for at most one request a second

    private static HttpClient CreateClient()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
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

    /// <summary>The cover as a frozen picture, or null (none found, offline, or not enough to search with).</summary>
    public static async Task<BitmapSource?> GetAsync(string path)
    {
        try
        {
            string key = KeyFor(path);
            string file = Path.Combine(Dir, key + ".jpg");
            string none = Path.Combine(Dir, key + ".none");

            if (File.Exists(file)) return Decode(await File.ReadAllBytesAsync(file).ConfigureAwait(false));
            if (File.Exists(none) && DateTime.UtcNow - File.GetLastWriteTimeUtc(none) < TimeSpan.FromDays(30)) return null;

            await OneAtATime.WaitAsync().ConfigureAwait(false);
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(25));
                var bytes = await FetchAsync(path, cts.Token).ConfigureAwait(false);
                Directory.CreateDirectory(Dir);
                if (bytes == null)
                {
                    File.WriteAllBytes(none, Array.Empty<byte>());
                    return null;
                }
                File.WriteAllBytes(file, bytes);
                await Task.Delay(1100).ConfigureAwait(false);
                return Decode(bytes);
            }
            finally { OneAtATime.Release(); }
        }
        catch (Exception ex)
        {
            AppLog.Write("Online cover", ex);   // offline or the service is busy: just no cover this time (nothing is remembered)
            return null;
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

    private static async Task<byte[]?> FetchAsync(string path, CancellationToken ct)
    {
        var tags = LibraryScanner.ReadTags(path);
        var fromName = OnlineLyrics.FromFileName(path);
        string artist = tags.Artist.Length > 0 ? tags.Artist : fromName.Artist;
        string title = tags.Title.Length > 0 ? tags.Title : fromName.Title;
        if (artist.Length == 0 || title.Length == 0) return null;   // a title alone would pick the wrong song too often

        static string Plain(string s) => s.Replace('"', ' ').Replace('\\', ' ');
        string query = $"recording:\"{Plain(title)}\" AND artist:\"{Plain(artist)}\"";
        using var response = await Http.GetAsync("https://musicbrainz.org/ws/2/recording/?fmt=json&limit=6&query=" + Uri.EscapeDataString(query), ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode) return null;

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
        if (!doc.RootElement.TryGetProperty("recordings", out var recordings) || recordings.ValueKind != JsonValueKind.Array) return null;

        // the releases (albums, singles) that contain the song; the one named like the album tag first
        var ids = new List<(string Id, bool SameAlbum)>();
        foreach (var recording in recordings.EnumerateArray())
        {
            if (!recording.TryGetProperty("releases", out var releases) || releases.ValueKind != JsonValueKind.Array) continue;
            foreach (var release in releases.EnumerateArray())
            {
                if (!release.TryGetProperty("id", out var id) || id.ValueKind != JsonValueKind.String) continue;
                string name = release.TryGetProperty("title", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString() ?? "" : "";
                ids.Add((id.GetString()!, tags.Album.Length > 0 && string.Equals(name, tags.Album, StringComparison.OrdinalIgnoreCase)));
            }
        }

        foreach (var (id, _) in ids.OrderByDescending(x => x.SameAlbum).Select(x => x).Distinct().Take(6))
        {
            ct.ThrowIfCancellationRequested();
            using var art = await Http.GetAsync($"https://coverartarchive.org/release/{id}/front-500", ct).ConfigureAwait(false);
            if (!art.IsSuccessStatusCode) continue;   // 404: this release has no front cover
            var bytes = await art.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
            if (bytes.Length > 1500) return bytes;
        }
        return null;
    }
}

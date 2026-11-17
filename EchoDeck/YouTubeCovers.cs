using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace EchoDeck;

/// <summary>
/// Last-resort cover source: the thumbnail of the song's YouTube video. Used for what no music catalogue lists (unofficial remixes,
/// bootlegs, new uploads) and when every other source came back empty. The video is found by the id in the file (a yt-dlp style
/// "Title [id].mp3" name, or a YouTube link in the tags) or, failing that, by searching YouTube for the artist and title; a search result
/// is only used when its title holds the words of the song and its length matches the file's. The 16:9 thumbnail is cut to a square
/// (a "Topic" upload's thumbnail is the album cover with black bars at the sides; a video's picture is cut to its middle).
/// Only the artist and title are sent. Off unless "Find missing covers online" is on, and never used in Anonymous Mode.
/// </summary>
internal static class YouTubeCovers
{
    private static readonly HttpClient Http = CreateClient();
    private static readonly Regex IdInName = new(@"\[([A-Za-z0-9_-]{11})\]", RegexOptions.Compiled);
    private static readonly Regex IdInLink = new(@"(?:youtu\.be/|youtube\.com/(?:watch\?(?:[^ ]*&)?v=|shorts/|embed/))([A-Za-z0-9_-]{11})", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex TitleIn = new(@"""title"":\{""runs"":\[\{""text"":""((?:\\.|[^""\\])*)""", RegexOptions.Compiled);
    private static readonly Regex LengthIn = new(@"""simpleText"":""(\d+(?::\d{2}){1,2})""", RegexOptions.Compiled);

    private static HttpClient CreateClient()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromSeconds(8) };
        http.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent",
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0 Safari/537.36");
        http.DefaultRequestHeaders.TryAddWithoutValidation("Accept-Language", "en-US,en;q=0.9");
        return http;
    }

    /// <summary>The YouTube video the file came from, when the file says so (name or tags).</summary>
    internal static string? IdFromFile(string path)
    {
        var inName = IdInName.Match(Path.GetFileNameWithoutExtension(path) ?? "");
        if (inName.Success) return inName.Groups[1].Value;

        try
        {
            var file = TagLib.File.Create(path, TagLib.ReadStyle.None);
            try
            {
                var link = IdInLink.Match((file.Tag?.Comment ?? ""));
                if (link.Success) return link.Groups[1].Value;
            }
            finally { (file as IDisposable)?.Dispose(); }
        }
        catch { /* no readable tags */ }
        return null;
    }

    private static double SecondsOf(string text)
    {
        double total = 0;
        foreach (var part in text.Split(':')) total = total * 60 + (double.TryParse(part, out var v) ? v : 0);
        return total;
    }

    private static string Unescape(string jsonText)
    {
        try { return JsonSerializer.Deserialize<string>("\"" + jsonText + "\"") ?? jsonText; }
        catch { return jsonText; }
    }

    /// <summary>The id of the video that is this song, from YouTube's search results (null when none matches well).</summary>
    internal static async Task<string?> SearchAsync(string artist, string title, double seconds, CancellationToken ct)
    {
        string term = (artist + " " + title).Trim();
        if (term.Length == 0) return null;

        string url = "https://www.youtube.com/results?hl=en&sp=EgIQAQ%253D%253D&search_query=" + Uri.EscapeDataString(term);
        using var response = await Http.GetAsync(url, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode) return null;
        string page = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

        var query = new SongQuery(artist, title, seconds, artist + " " + title);
        string? bestId = null;
        double bestGap = double.MaxValue;

        var chunks = page.Split(new[] { "\"videoRenderer\":{\"videoId\":\"" }, StringSplitOptions.None);
        for (int i = 1; i < chunks.Length && i <= 12; i++)
        {
            string chunk = chunks[i];
            if (chunk.Length < 12) continue;
            string id = chunk.Substring(0, 11);

            var t = TitleIn.Match(chunk);
            if (!t.Success) continue;
            string videoTitle = Unescape(t.Groups[1].Value);
            var videoWords = SongText.Tokens(videoTitle);

            // the video's title must hold (nearly) every word of the song, and not be a karaoke / live / cover when the file is not
            int hit = query.Clean.Count(w => SongText.ContainsToken(videoWords, w));
            if (query.Clean.Count == 0 || hit / (double)query.Clean.Count < 0.8) continue;
            if (!query.JunkWanted && SongText.Junk.IsMatch(videoTitle)) continue;

            double gap = 0;
            var len = LengthIn.Match(chunk);
            if (seconds > 0 && len.Success)
            {
                gap = Math.Abs(SecondsOf(len.Groups[1].Value) - seconds);
                if (gap > 12) continue;   // a different recording (or a long mix)
            }
            if (gap < bestGap) { bestGap = gap; bestId = id; }
        }
        return bestId;
    }

    /// <summary>The thumbnail of a video as a square JPEG (null when it has none).</summary>
    internal static async Task<(string Url, byte[] Bytes)?> ThumbnailAsync(string id, CancellationToken ct)
    {
        foreach (var name in new[] { "maxresdefault", "hq720", "hqdefault" })
        {
            string url = $"https://i.ytimg.com/vi/{id}/{name}.jpg";
            try
            {
                var data = await Http.GetByteArrayAsync(url, ct).ConfigureAwait(false);
                if (data.Length < 3000) continue;   // YouTube's grey placeholder for a missing size
                var square = CropSquare(data);
                if (square != null) return (url, square);
            }
            catch (HttpRequestException) { /* that size does not exist: try the next */ }
        }
        return null;
    }

    /// <summary>
    /// The picture inside black bars, when there are bars and what is inside them is about square (a cover art upload: the cover in
    /// the middle of a black 16:9 frame). Null for an ordinary video frame, which is cut to its middle instead.
    /// </summary>
    private static Int32Rect? ArtworkBounds(BitmapSource frame)
    {
        try
        {
            var bgra = new FormatConvertedBitmap(frame, PixelFormats.Bgra32, null, 0);
            int w = bgra.PixelWidth, h = bgra.PixelHeight, stride = w * 4;
            var px = new byte[stride * h];
            bgra.CopyPixels(px, stride, 0);

            bool Dark(int x, int y) { int i = y * stride + x * 4; return px[i] + px[i + 1] + px[i + 2] <= 3 * 28; }

            bool ColumnDark(int x, int y0, int y1)
            {
                int dark = 0, total = 0;
                for (int y = y0; y <= y1; y += 3) { total++; if (Dark(x, y)) dark++; }
                return dark >= total * 0.97;
            }

            bool RowDark(int y, int x0, int x1)
            {
                int dark = 0, total = 0;
                for (int x = x0; x <= x1; x += 3) { total++; if (Dark(x, y)) dark++; }
                return dark >= total * 0.97;
            }

            int left = 0, right = w - 1, top = 0, bottom = h - 1;
            while (left < right && ColumnDark(left, 0, h - 1)) left++;
            while (right > left && ColumnDark(right, 0, h - 1)) right--;
            while (top < bottom && RowDark(top, left, right)) top++;
            while (bottom > top && RowDark(bottom, left, right)) bottom--;

            int cw = right - left + 1, ch = bottom - top + 1;
            bool hasBars = left > w * 0.03 || w - 1 - right > w * 0.03 || top > h * 0.03 || h - 1 - bottom > h * 0.03;
            double aspect = (double)cw / Math.Max(1, ch);
            if (!hasBars || cw < w * 0.3 || ch < h * 0.3 || aspect < 0.85 || aspect > 1.18) return null;

            // a pixel or two in from the edge: the border of the cover is not part of it
            int trim = 2;
            return new Int32Rect(left + trim, top + trim, Math.Max(16, cw - 2 * trim), Math.Max(16, ch - 2 * trim));
        }
        catch { return null; }
    }

    /// <summary>The middle square of a 16:9 thumbnail (hqdefault is 16:9 with black bars above and below: the bars are left out).</summary>
    internal static byte[]? CropSquare(byte[] data)
    {
        try
        {
            using var input = new MemoryStream(data);
            var frame = BitmapDecoder.Create(input, BitmapCreateOptions.None, BitmapCacheOption.OnLoad).Frames[0];
            int w = frame.PixelWidth, h = frame.PixelHeight;
            if (w < 64 || h < 64) return null;

            Int32Rect area;
            if (ArtworkBounds(frame) is { } art)
            {
                // black bars around a roughly square picture (the album cover of a "Topic" upload): cut to the cover itself
                int side = Math.Min(art.Width, art.Height);
                area = new Int32Rect(art.X + (art.Width - side) / 2, art.Y + (art.Height - side) / 2, side, side);
            }
            else
            {
                int pictureHeight = (double)w / h < 1.5 ? (int)(w * 9.0 / 16) : h;
                int side = Math.Min(pictureHeight, w);
                area = new Int32Rect((w - side) / 2, (h - side) / 2, side, side);
            }

            var encoder = new JpegBitmapEncoder { QualityLevel = 92 };
            encoder.Frames.Add(BitmapFrame.Create(new CroppedBitmap(frame, area)));
            using var output = new MemoryStream();
            encoder.Save(output);
            return output.ToArray();
        }
        catch { return null; }
    }
}

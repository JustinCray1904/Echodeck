using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;

namespace EchoDeck;

/// <summary>
/// Resolves a local tag/filename against Apple's public catalog before querying lyrics or artwork providers.
/// This is metadata-only: no audio is uploaded. If the catalog is unavailable or has no confident match,
/// callers retain the cleaned local identity.
/// </summary>
internal static class OnlineTrackIdentity
{
    internal sealed record Match(string Artist, string Title, double DurationSeconds, string ArtworkUrl);

    private static readonly HttpClient Http = CreateClient();
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, Match> Cache = new(StringComparer.Ordinal);

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(7) };
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("Tempo", "3.1"));
        return client;
    }

    internal static async Task<Match?> ResolveAsync(string artist, string title, double durationSeconds, CancellationToken ct)
    {
        artist = DisplayNames.Tidy(artist);
        title = DisplayNames.Tidy(title);
        if (title.Length == 0) return null;
        string cacheKey = TextMatch.Norm(artist) + "|" + TextMatch.Norm(title) + "|" + Math.Round(durationSeconds).ToString(System.Globalization.CultureInfo.InvariantCulture);
        if (Cache.TryGetValue(cacheKey, out var cached)) return cached;

        try
        {
            var terms = new List<string>();
            if (artist.Length > 0) terms.Add(artist + " " + title);
            terms.Add(title); // fallback for catalog search tokenization, still validated against artist below

            Match? best = null;
            double bestScore = double.MaxValue;
            foreach (string term in terms.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                string url = "https://itunes.apple.com/search?media=music&entity=song&limit=25&term=" + Uri.EscapeDataString(term);
                using var response = await Http.GetAsync(url, ct).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode) continue;
                using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
                if (!doc.RootElement.TryGetProperty("results", out var results) || results.ValueKind != JsonValueKind.Array) continue;

                foreach (var item in results.EnumerateArray())
                {
                    string foundTitle = Read(item, "trackName");
                    string foundArtist = Read(item, "artistName");
                    if (TextMatch.Norm(title).Length == 0 || TextMatch.Norm(title) != TextMatch.Norm(foundTitle)) continue;
                    if (artist.Length > 0 && !TextMatch.SameArtist(artist, foundArtist)) continue;

                    double foundDuration = item.TryGetProperty("trackTimeMillis", out var ms) && ms.ValueKind == JsonValueKind.Number
                        ? ms.GetDouble() / 1000.0 : 0;
                    double diff = durationSeconds > 0 && foundDuration > 0 ? Math.Abs(durationSeconds - foundDuration) : 30;
                    if (durationSeconds > 0 && foundDuration > 0 && diff > 18) continue;
                    double score = diff + (foundDuration <= 0 ? 25 : 0);
                    if (score >= bestScore) continue;
                    bestScore = score;
                    best = new Match(foundArtist, foundTitle, foundDuration, Read(item, "artworkUrl100"));
                }
                if (best != null && bestScore <= 1.5) break;
            }
            if (best != null) Cache[cacheKey] = best;
            return best;
        }
        catch (OperationCanceledException) { throw; }
        catch { return null; }
    }

    private static string Read(JsonElement item, string key) =>
        item.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : "";
}
